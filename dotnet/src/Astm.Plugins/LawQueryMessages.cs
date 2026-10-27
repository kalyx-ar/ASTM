using Astm.Protocol.Hl7v2;

namespace Astm.Plugins;

/// <summary>LAB-27 query primitives (LAW rev. 1.4 §3.Q.5). An RSP is only the query acknowledgement;
/// AWOS dispatch is a separate LAB-28 transaction. This is not a complete LAW actor.</summary>
public static class LawQueryMessages
{
    public static string ContainerId(Hl7Message query)
    {
        if (query.Type != "QBP" || query.Trigger != "Q11" || query.Header.Text(9, 3) != "QBP_Q11"
            || query.Header.Field(21) != "LAB-27" + query.Delimiters.Component + "IHE") throw new FormatException("ASTM-LAW-INVALID-PROFILE");
        var parameters = query.OfType("QPD").ToArray();
        if (parameters.Length != 1) throw new FormatException("ASTM-LAW-INVALID-QUERY");
        var qpd = parameters[0];
        if (qpd.Text(1) != "WOS" || qpd.Text(3).Length == 0 || query.OfType("RCP").Count() != 1
            || Enumerable.Range(4, 6).Any(i => qpd.Field(i).Length > 0))
            throw new FormatException("ASTM-LAW-UNSUPPORTED-QUERY");
        return qpd.Text(3);
    }

    public static Hl7Message Acknowledge(Hl7Message query, string controlId, DateTimeOffset now, bool accepted = true)
    {
        _ = ContainerId(query);
        var d = query.Delimiters;
        var header = Hl7Acknowledgements.ResponseHeader(query, "RSP", "K11", "RSP_K11", controlId, now);
        var fields = Enumerable.Range(3, 19).Select(i => (i, i == 21 ? "LAB-27" + d.Component + "IHE" : header.Field(i))).ToArray();
        var segments = new List<Hl7Segment> { Hl7Segment.Build("MSH", d, fields),
            Hl7Segment.Build("MSA", d, (1, accepted ? "AA" : "AE"), (2, d.Encode(query.ControlId))) };
        if (!accepted) segments.Add(Hl7Segment.Build("ERR", d, (3, "207" + d.Component + "Query unavailable" + d.Component + "HL70357"), (4, "E")));
        var qpd = query.OfType("QPD").Single();
        segments.Add(Hl7Segment.Build("QAK", d, (1, qpd.Field(2)), (2, accepted ? "OK" : "AE"), (3, qpd.Field(1))));
        segments.Add(qpd);
        return new(d, segments);
    }
}
