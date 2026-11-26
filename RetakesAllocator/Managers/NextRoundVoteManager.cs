using RetakesAllocatorCore;
using RetakesAllocatorCore.Managers;

namespace RetakesAllocator.Managers;

public class NextRoundVoteManager : AbstractVoteManager
{
    private readonly IEnumerable<string> _options = RoundTypeHelpers
        .GetRoundTypes()
        .Select(r => r.ToString());

    public NextRoundVoteManager() : base("vote.subject.next_round", "!nextround")
    {
    }


    public override IEnumerable<string> GetVoteOptions()
    {
        return _options;
    }

    protected override void HandleVoteResult(string option)
    {
        if (RoundTypeHelpers.ParseRoundType(option) is not { } roundType)
        {
            return;
        }

        RoundTypeManager.Instance.SetNextRoundTypeOverride(roundType);
        PrintToServer(Translator.Instance["vote.complete_next_round", RoundTypeHelpers.TranslateRoundTypeName(roundType)]);
    }
}
