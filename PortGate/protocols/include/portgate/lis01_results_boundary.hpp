#pragma once
#include "portgate/virtual_com_protocol.hpp"
namespace portgate {
/// Opt-in profile: a single instrument results batch, H/R/L records, explicitly acknowledged frames then EOT.
/// This is NOT a proof for query/worklist or a multi-turn business workflow. Unknown/corrupted traffic blocks it.
class Lis01ResultsBoundary final : public OperationBoundary {
public:
    void observe(bool from_instrument, const link::Bytes& bytes) override;
    bool complete() const override { return safe_ && !unknown_; }
private:
    void byte(bool instrument, std::uint8_t b);
    void frame();
    void fail() { unknown_=true; safe_=false; }
    link::Bytes frame_, message_, pending_;
    bool collecting_=false, frame_side_=false, unknown_=false, safe_=false;
    bool enquiry_=false, established_=false, awaiting_ack_=false, pending_final_=false, saw_header_=false, saw_result_=false, saw_terminator_=false;
    int expected_=1;
};
}
