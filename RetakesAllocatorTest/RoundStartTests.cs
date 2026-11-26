using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Db;

namespace RetakesAllocatorTest;

public class RoundStartTests : BaseTestFixture
{
    [Test]
    public void PistolRoundGivesOneDefuseKitEvenWhenSteamIdsAreEqual()
    {
        var recipients = new List<int>();
        OnRoundPostStartHelper.Handle(new[] { 1, 2 }, _ => 0, _ => CsTeam.CounterTerrorist,
            recipients.Add, (_, _, _) => { }, _ => true, _ => true, _ => true,
            RoundType.Pistol, new Dictionary<ulong, PreferenceSnapshot>());
        Assert.That(recipients, Has.Count.EqualTo(1));
    }

    [Test]
    public void TestRoundStartCanRunInCore()
    {
        OnRoundPostStartHelper.Handle(
            new List<int>(),
            i => 1,
            x => CsTeam.None,
            x => {},
            (x, y, z) => {},
            x => true,
            x => true,
            x => true,
            RoundType.Pistol,
            new Dictionary<ulong, PreferenceSnapshot>()
        );
    }
}
