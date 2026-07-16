using ProcioneMGR.Contracts.Common.V1;

// Vive accanto al dominio trading, unico consumer di DecimalValue: un namespace
// ProcioneMGR.Services.Contracts avrebbe messo in ombra ProcioneMGR.Contracts dentro TUTTI i
// ProcioneMGR.Services.*, rendendo ambiguo ogni "Contracts.*" scritto lì dentro. Se un secondo
// dominio avrà bisogno di questa conversione, si promuove allora (con un nome che non collida).
namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Conversione fra <see cref="decimal"/> di dominio e il wrapper <see cref="DecimalValue"/> di
/// common.proto (convenzione google.type.Decimal-like: value = units + nanos/1e9, con nanos dello
/// stesso segno di units). Esiste per non spargere aritmetica su units/nanos nei mapper: sbagliare
/// il segno dei nanos su un PnL negativo darebbe un errore di 2× il valore, silenzioso.
///
/// LIMITE NOTO — la rappresentazione tiene 9 cifre decimali; <see cref="decimal"/> ne tiene fino a
/// 28. Oltre la nona, <see cref="ToProto"/> ARROTONDA (mai tronca: l'arrotondamento è simmetrico e
/// non introduce bias sistematico sui PnL). È accettabile qui perché su questo filo passano stato e
/// reportistica (prezzi/quantità exchange stanno negli 8 decimali; il resto sono grandezze
/// statistiche o da mostrare a schermo), NON il piazzamento degli ordini: quello avviene tutto
/// dentro procionemgr-trading, dove i decimal non attraversano mai protobuf. Se un domani un
/// importo con più di 9 decimali dovesse diventare vincolante, la strada è cambiare
/// <c>DecimalValue</c> in una stringa (come fa google.type.Decimal), non allargare i nanos.
/// </summary>
public static class DecimalValueMapper
{
    private const decimal NanosPerUnit = 1_000_000_000m;

    public static DecimalValue ToProto(decimal value)
    {
        // Arrotonda PRIMA di separare units/nanos: così il carry (es. 0.9999999999 → 1.0) avviene
        // una volta sola e non può produrre nanos == ±1e9, che violerebbe l'invariante del formato.
        var rounded = Math.Round(value, 9, MidpointRounding.AwayFromZero);

        var units = decimal.Truncate(rounded);
        if (units < long.MinValue || units > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value,
                "Valore fuori dall'intervallo rappresentabile da DecimalValue (units è un int64).");
        }

        // Truncate su entrambi: la parte frazionaria ha già al più 9 decimali dopo l'arrotondamento,
        // quindi qui non si perde nulla. decimal.Truncate mantiene il segno => nanos ha lo stesso
        // segno di units, come vuole la convenzione.
        var nanos = decimal.Truncate((rounded - units) * NanosPerUnit);

        return new DecimalValue { Units = (long)units, Nanos = (int)nanos };
    }

    /// <summary>Campo proto assente => <c>null</c> (has-bit dei message: distingue "non impostato" da 0).</summary>
    public static DecimalValue? ToProtoNullable(decimal? value) => value is null ? null : ToProto(value.Value);

    public static decimal FromProto(DecimalValue value)
    {
        // Segni discordi = valore fuori formato. Convertirlo comunque darebbe un importo sbagliato in
        // silenzio: {units:-1, nanos:+750M} verrebbe letto -0.25 invece di -1.75. Su un PnL è meglio
        // fallire rumorosamente che restituire un numero plausibile e falso. ToProto costruisce
        // sempre valori concordi: qui ci arriva solo un mittente non conforme (o un futuro mapper
        // che sbaglia), ed è esattamente quello che vogliamo scoprire subito.
        if ((value.Units > 0 && value.Nanos < 0) || (value.Units < 0 && value.Nanos > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"units={value.Units} nanos={value.Nanos}",
                "DecimalValue malformato: units e nanos devono avere lo stesso segno (convenzione di common.proto).");
        }
        if (value.Nanos <= -1_000_000_000 || value.Nanos >= 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.Nanos,
                "DecimalValue malformato: |nanos| deve essere < 1e9 (la parte intera sta in units).");
        }

        return value.Units + value.Nanos / NanosPerUnit;
    }

    public static decimal? FromProtoNullable(DecimalValue? value) => value is null ? null : FromProto(value);

    /// <summary>Per i campi non opzionali: un message assente sul wire vale 0 (default proto3).</summary>
    public static decimal FromProtoOrZero(DecimalValue? value) => value is null ? 0m : FromProto(value);
}
