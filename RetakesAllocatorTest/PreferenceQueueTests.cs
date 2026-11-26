using System.Threading.Tasks;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;

namespace RetakesAllocatorTest;

public class PreferenceQueueTests : BaseTestFixture
{
    [Test]
    public async Task RapidZeusTogglesAreAppliedInOrder()
    {
        const ulong id = 76561198000000001;
        var first = Queries.ToggleZeusAsync(id);
        var second = Queries.ToggleZeusAsync(id);
        Assert.That(await first, Is.True);
        Assert.That(await second, Is.False);
        Assert.That(Queries.IsZeusEnabled(id), Is.False);
    }

    [Test]
    public async Task PreferenceWritesPreserveOtherSettingsAndPublishAnIndependentSnapshot()
    {
        const ulong id = 76561198000000002;
        await Task.WhenAll(
            Queries.SetWeaponPreferenceForUserAsync(id, CsTeam.Terrorist, WeaponAllocationType.PistolRound, CsItem.Deagle),
            Queries.SetEnemyStuffPreferenceAsync(id, EnemyStuffTeamPreference.Both));
        var snapshot = Queries.ReadSnapshot(id)!;
        Assert.That(snapshot.EnemyStuffTeamPreference, Is.EqualTo(EnemyStuffTeamPreference.Both));
        Assert.That(snapshot.GetWeaponPreference(CsTeam.Terrorist, WeaponAllocationType.PistolRound), Is.EqualTo(CsItem.Deagle));
        var stored = await Queries.GetUserSettings(id);
        stored!.WeaponPreferences.Clear();
        Assert.That(Queries.ReadSnapshot(id)!.GetWeaponPreference(CsTeam.Terrorist, WeaponAllocationType.PistolRound),
            Is.EqualTo(CsItem.Deagle));
    }

    [Test]
    public async Task PrefetchPublishesExternalChanges()
    {
        const ulong id = 76561198000000004;
        await using (var database = new Db())
        {
            database.UserSettings.Add(new UserSetting { UserId = id, ZeusEnabled = true });
            await database.SaveChangesAsync();
        }
        Queries.Prefetch([id, id]);
        for (var attempt = 0; !Queries.HasSnapshot(id) && attempt < 100; attempt++)
            await Task.Delay(10);
        Assert.That(Queries.IsZeusEnabled(id), Is.True);
    }

    [Test]
    public async Task MissingRowDoesNotLookLikeAnUnknownSnapshot()
    {
        const ulong id = 76561198000000003;
        Assert.That(Queries.HasSnapshot(id), Is.False);
        Assert.That(await Queries.GetUserSettings(id), Is.Null);
        Assert.That(Queries.HasSnapshot(id), Is.True);
        Assert.That(Queries.IsZeusEnabled(id), Is.False);
    }
}
