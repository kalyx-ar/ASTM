#include <iostream>
#include "vcom_setup.hpp"
namespace portgate {
int virtual_com_command(const std::vector<std::string>&) {std::cerr<<"Virtual COM/TTY is not offered on this build/platform.\n";return 69;}
}
