using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Discovery;

namespace ProcioneMGR.Tests;

/// <summary>
/// Invariante di integrazione: OGNI strategia registrata in <see cref="StrategyFactory"/> deve
/// avere una griglia di default in <see cref="StrategyDiscoveryEngine.DefaultRanges"/>, con nomi
/// parametro esistenti nelle <c>ParameterDefinitions</c> della strategia. Senza questo, una
/// nuova strategia appare selezionabile in Discovery ma lo sweep non produce nulla (bug reale
/// trovato in revisione: DonchianBreakout e PriceSmaCross erano scoperti).
/// </summary>
public class StrategyDiscoveryDefaultsTests
{
    [Fact]
    public void EveryFactoryStrategy_HasDefaultRanges_WithValidParameterNames()
    {
        var factory = new StrategyFactory();

        foreach (var prototype in factory.Prototypes)
        {
            var ranges = StrategyDiscoveryEngine.DefaultRanges(prototype.Name);
            Assert.True(ranges.Count > 0,
                $"La strategia '{prototype.Name}' non ha griglie di default in Discovery.");

            var validNames = prototype.ParameterDefinitions.Select(d => d.Key).ToHashSet();
            foreach (var range in ranges)
            {
                Assert.True(validNames.Contains(range.Name),
                    $"La griglia di '{prototype.Name}' usa il parametro '{range.Name}' che non esiste " +
                    $"nelle ParameterDefinitions ({string.Join(", ", validNames)}).");
                Assert.True(range.Min <= range.Max, $"{prototype.Name}.{range.Name}: Min > Max.");
                Assert.True(range.Step > 0, $"{prototype.Name}.{range.Name}: Step <= 0.");

                // I valori della griglia devono rientrare nei limiti dichiarati dalla strategia.
                var def = prototype.ParameterDefinitions.First(d => d.Key == range.Name);
                Assert.True(range.Min >= def.Min && range.Max <= def.Max,
                    $"{prototype.Name}.{range.Name}: griglia [{range.Min}-{range.Max}] fuori dai " +
                    $"limiti della strategia [{def.Min}-{def.Max}].");
            }
        }
    }
}
