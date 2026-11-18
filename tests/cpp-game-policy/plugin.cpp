#include <gua/gua.h>
extern "C" __declspec(dllexport) int plugin_value(){char text[8192];return gua_copy_build_info_json(text,sizeof(text));}
#ifdef EARLY_PLUGIN
struct Early {Early(){plugin_value();}};
static Early early;
#endif
