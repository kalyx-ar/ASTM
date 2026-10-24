namespace Atsm.Protocol.Hl7v2;

public enum Hl7AcknowledgementPhase { Commit, Application }
public sealed record Hl7Acknowledgement(string Code, string ControlId)
{
    public bool Accepted => Code is "AA" or "CA";
    public Hl7AcknowledgementPhase Phase => Code.StartsWith('C') ? Hl7AcknowledgementPhase.Commit : Hl7AcknowledgementPhase.Application;
}

public static class Hl7Acknowledgements
{
    public static Hl7Acknowledgement? Read(Hl7Message message)
    {
        var msa = message.OfType("MSA").FirstOrDefault();
        if (msa is null || msa.Text(2).Length == 0 || msa.Text(1) is not ("AA" or "AE" or "AR" or "CA" or "CE" or "CR")) return null;
        return new(msa.Text(1), msa.Text(2));
    }

    public static bool Required(Hl7Message message, Hl7AcknowledgementPhase phase, bool success)
    {
        if (message.Type == "ACK") return false;
        var header = message.Header;
        if (header.Field(15).Length == 0 && header.Field(16).Length == 0) return phase == Hl7AcknowledgementPhase.Application;
        return header.Field(phase == Hl7AcknowledgementPhase.Commit ? 15 : 16) switch
        { "AL" => true, "SU" => success, "ER" => !success, "NE" or "" => false, _ => throw new FormatException("ATSM-HL7-INVALID-ACK-POLICY") };
    }

    public static Hl7Segment ResponseHeader(Hl7Message request, string type, string trigger, string structure, string controlId, DateTimeOffset now)
    {
        var d = request.Delimiters;
        return Hl7Segment.Build("MSH", d, (3, request.Header.Field(5)), (4, request.Header.Field(6)),
            (5, request.Header.Field(3)), (6, request.Header.Field(4)), (7, now.ToString("yyyyMMddHHmmsszzz", System.Globalization.CultureInfo.InvariantCulture).Replace(":", "")),
            (9, string.Join(d.Component, type, trigger, structure)), (10, d.Encode(controlId)),
            (11, request.Header.Field(11)), (12, request.Header.Field(12)), (18, request.Header.Field(18)));
    }

    public static Hl7Message Build(Hl7Message request, string code, string controlId, DateTimeOffset now, string? errorCode = null)
    {
        if (code is not ("AA" or "AE" or "AR" or "CA" or "CE" or "CR")) throw new ArgumentOutOfRangeException(nameof(code));
        var d = request.Delimiters;
        var segments = new List<Hl7Segment>
        {
            ResponseHeader(request, "ACK", request.Trigger, "ACK", controlId, now),
            Hl7Segment.Build("MSA", d, (1, code), (2, d.Encode(request.ControlId))),
        };
        if (errorCode is not null) segments.Add(Hl7Segment.Build("ERR", d, (3, "207" + d.Component + d.Encode(errorCode) + d.Component + "HL70357"), (4, "E")));
        return new(d, segments);
    }
}
