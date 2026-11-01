#include "gua/runtime.h"
#include <iostream>
#include <vector>
int main() {
    const int size=gua_runtime_copy_build_info_json(nullptr,0);
    if(size<=0 || size>65536) return 1;
    std::vector<char> json(static_cast<size_t>(size));
    if(gua_runtime_copy_build_info_json(json.data(),size)!=size) return 2;
    std::cout << json.data() << '\n';
}
