using Atsm.Abstractions.Model;
using Atsm.Protocol.Lis02;

namespace Atsm.Plugins;

/// <summary>Optional semantic presentation; originals and protocol decisions remain separate.</summary>
public interface IInstrumentMessageKnowledge
{
    ProtocolExplanation? ExplainMessage(Lis02Message message);
}

public sealed record InstrumentFlagMeaning(ProtocolText Text, bool RequiresAttention, bool IsError = false);
public interface IInstrumentFlagKnowledge
{
    InstrumentFlagMeaning? DescribeFlag(InstrumentObservation observation, CodedValue flag);
    ProtocolText? DescribeState(InstrumentObservation observation) => null;
}
