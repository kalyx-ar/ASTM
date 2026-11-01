#include "portgate/lis01_results_boundary.hpp"
namespace portgate {
void Lis01ResultsBoundary::observe(bool instrument, const link::Bytes& bytes) {
    for (auto b:bytes) byte(instrument,b);
}
void Lis01ResultsBoundary::byte(bool instrument, std::uint8_t b) {
    if (unknown_) return;
    if (collecting_) {
        if (instrument!=frame_side_ || frame_.size()>=65536) { fail(); return; }
        frame_.push_back(b);
        if (b==0x0a) { collecting_=false; frame(); }
        return;
    }
    safe_=false;
    if (b==0x05 && instrument && !established_ && !enquiry_) { enquiry_=true; return; }
    if (b==0x06 && !instrument && enquiry_) { enquiry_=false; established_=true; expected_=1; return; }
    if (b==0x02 && instrument && established_ && !awaiting_ack_) {
        collecting_=true; frame_side_=instrument; frame_={b}; return;
    }
    if (b==0x15 && !instrument && established_ && awaiting_ack_) { awaiting_ack_=false; pending_.clear(); return; }
    if (b==0x06 && !instrument && established_ && awaiting_ack_) {
        awaiting_ack_=false; expected_=(expected_+1)%8;
        message_.insert(message_.end(),pending_.begin(),pending_.end()); pending_.clear();
        if (message_.size()>65536) { fail(); return; }
        if (pending_final_) {
            std::string record;
            for (auto c:message_) {
                if (c==0x0d) {
                    if (record.size()<2 || record[1]!='|') { fail(); return; }
                    if(std::string("HPORCL").find(record[0])==std::string::npos || (record[0]=='H' && saw_header_)) { fail(); return; }
                    if (!saw_header_ && record[0]!='H') { fail(); return; }
                    if (saw_terminator_ || record[0]=='Q') { fail(); return; }
                    saw_header_=saw_header_ || record[0]=='H'; saw_result_=saw_result_ || record[0]=='R';
                    if(record[0]=='L' && record!="L|1|N") { fail(); return; }
                    saw_terminator_=record[0]=='L'; record.clear();
                } else if (c>=0x20 && c<=0x7e) record.push_back(static_cast<char>(c));
                else { fail(); return; }
            }
            if (!record.empty()) { fail(); return; }
            message_.clear();
        }
        return;
    }
    if (b==0x04 && instrument && established_ && !awaiting_ack_ && message_.empty() && saw_header_ && saw_result_ && saw_terminator_) {
        established_=false; safe_=true;
        saw_header_=saw_result_=saw_terminator_=false;
        return;
    }
    fail();
}
void Lis01ResultsBoundary::frame() {
    if (frame_.size()<7 || frame_[1] != static_cast<std::uint8_t>('0'+expected_) || frame_[frame_.size()-2]!=0x0d) { fail(); return; }
    const auto end=frame_.size()-5;
    if (frame_[end]!=0x03 && frame_[end]!=0x17) { fail(); return; }
    auto hex=[](std::uint8_t c) { return c>='0'&&c<='9' ? c-'0' : c>='A'&&c<='F' ? c-'A'+10 : -1; };
    const auto hi=hex(frame_[end+1]), lo=hex(frame_[end+2]);
    unsigned sum=0; for (std::size_t i=1;i<=end;++i) sum+=frame_[i];
    if (hi<0 || lo<0 || (sum&255)!=static_cast<unsigned>(hi*16+lo)) { fail(); return; }
    pending_.assign(frame_.begin()+2,frame_.begin()+static_cast<std::ptrdiff_t>(end));
    pending_final_=frame_[end]==0x03; awaiting_ack_=true;
}
}
