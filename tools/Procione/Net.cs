using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Procione;

/// <summary>
/// Chi ascolta su quale porta, chiesto al sistema DENTRO questo processo.
///
/// PERCHE' NON PASSA PIU' DA POWERSHELL (2026-09-06). La mappa porta → PID si otteneva con
/// <c>Get-NetTCPConnection</c> lanciato in un processo figlio. Su una macchina satura quel
/// processo non risponde in tempo, e il difetto che ne nasce non e' un errore: e' una RISPOSTA
/// SBAGLIATA. E' successo alle 00:54 del 2026-09-05 — la query non ha risposto, la lista e'
/// tornata vuota, `ferma guscio` ha letto «gia' fermo», il bring-up ha trovato la porta ancora
/// occupata e ha concluso «gia' in ascolto», e il sync ha annunciato «aggiornato e riavviato»
/// mentre il guscio era quello di undici ore prima. Un rilascio finto.
///
/// La correzione di quel giorno ha insegnato al chiamante a distinguere «nessuno» da «non lo so».
/// Questa toglie la domanda di mezzo: <c>GetExtendedTcpTable</c> e' una chiamata di libreria, non
/// un processo — non ha un tetto di tempo da sforare, non compete per la memoria, e sulla macchina
/// piu' carica risponde come su quella scarica. Resta comunque il valore <c>null</c> per il caso
/// in cui l'API fallisca davvero: «non lo so» deve restare dicibile.
/// </summary>
internal static class Net
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;

    /// TCP_TABLE_OWNER_PID_LISTENER: solo i socket in ascolto, con il PID di chi li possiede.
    private const int TableOwnerPidListener = 3;

    private const int NoError = 0;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tabella, ref int dimensione, bool ordina,
                                                   int famiglia, int classe, int riservato);

    /// <summary>
    /// I PID in ascolto su una porta, su IPv4 e IPv6 insieme.
    ///
    /// <c>null</c> significa che la domanda NON ha avuto risposta — e' un'altra cosa da «nessuno
    /// ascolta», e chi chiama deve poterle distinguere: dichiarare fermo cio' che non si e' potuto
    /// misurare e' il difetto che questa classe esiste per non ripetere.
    ///
    /// Le due famiglie si interrogano entrambe perche' <c>kubectl port-forward</c> lega
    /// «localhost», che su questa macchina e' sia 127.0.0.1 sia [::1]: guardare solo IPv4
    /// lascerebbe vivo meta' tunnel dopo un `ferma tunnel` che si dichiara riuscito.
    /// </summary>
    public static List<int>? OwningPids(int porta)
    {
        var v4 = Listeners(AfInet);
        var v6 = Listeners(AfInet6);

        // Se ENTRAMBE falliscono non si sa nulla. Se ne fallisce una sola si risponde con l'altra:
        // meglio una risposta parziale dichiarata dal chiamante (che riverifica sulle porte) che
        // un «non lo so» su una macchina in cui IPv6 e' disattivato.
        if (v4 is null && v6 is null) return null;

        return (v4 ?? []).Concat(v6 ?? [])
            .Where(r => r.Porta == porta)
            .Select(r => r.Pid)
            .Where(p => p > 0)
            .Distinct()
            .ToList();
    }

    /// <summary>Le porte in ascolto. Non serve il PID, quindi basta l'API gestita.</summary>
    public static HashSet<int> ListeningPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        }
        catch { return []; }
    }

    private static List<(int Porta, int Pid)>? Listeners(int famiglia)
    {
        var dimensione = 0;
        // Primo giro a buffer nullo: l'API dice quanto serve. Fra la misura e l'allocazione le
        // connessioni possono cambiare, quindi si riprova qualche volta invece di arrendersi.
        for (var tentativo = 0; tentativo < 4; tentativo++)
        {
            var esito = GetExtendedTcpTable(IntPtr.Zero, ref dimensione, false, famiglia, TableOwnerPidListener, 0);
            if (esito != ErrorInsufficientBuffer && esito != NoError) return null;
            if (dimensione <= 0) return [];

            var buffer = Marshal.AllocHGlobal(dimensione);
            try
            {
                esito = GetExtendedTcpTable(buffer, ref dimensione, false, famiglia, TableOwnerPidListener, 0);
                if (esito == ErrorInsufficientBuffer) continue;   // cresciuta nel frattempo: si rifa'
                if (esito != NoError) return null;
                return Leggi(buffer, famiglia);
            }
            catch { return null; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    // Le due strutture di Windows, misurate in byte perche' e' cosi' che arrivano.
    //   MIB_TCPROW_OWNER_PID   (IPv4, 24 byte): stato, indirizzo, porta, remoto, porta remota, pid
    //   MIB_TCP6ROW_OWNER_PID  (IPv6, 56 byte): indirizzo[16], scope, porta, remoto[16], scope, porta, stato, pid
    private const int RigaV4 = 24;
    private const int RigaV6 = 56;

    private static List<(int Porta, int Pid)> Leggi(IntPtr buffer, int famiglia)
    {
        var righe = new List<(int, int)>();
        var quante = Marshal.ReadInt32(buffer);
        var passo = famiglia == AfInet ? RigaV4 : RigaV6;
        // dwNumEntries e' seguito dalla tabella; su x64 la struttura e' allineata a 4, quindi la
        // prima riga comincia subito dopo il conteggio.
        var origine = IntPtr.Add(buffer, 4);

        for (var i = 0; i < quante; i++)
        {
            var riga = IntPtr.Add(origine, i * passo);
            // La porta e' un DWORD che contiene un valore a 16 bit in ORDINE DI RETE: i due byte
            // vanno letti nell'ordine in cui stanno, non come intero little-endian. Leggerli come
            // int darebbe 20563 al posto di 5199, e nessun comando troverebbe mai niente.
            var (scartoPorta, scartoPid) = famiglia == AfInet ? (8, 20) : (20, 52);
            var alto = Marshal.ReadByte(riga, scartoPorta);
            var basso = Marshal.ReadByte(riga, scartoPorta + 1);
            righe.Add(((alto << 8) | basso, Marshal.ReadInt32(riga, scartoPid)));
        }
        return righe;
    }
}
