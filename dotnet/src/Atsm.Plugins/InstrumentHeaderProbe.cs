using Atsm.Protocol.Lis02;

namespace Atsm.Plugins;

/// <summary>Optional, plugin-owned identity evidence from an ASTM sender header. A match identifies
/// a dialect, not installed options, firmware qualification or permission to send commands.</summary>
public interface IInstrumentHeaderProbe
{
    bool RecognizesHeader(Lis02Record header);
}
