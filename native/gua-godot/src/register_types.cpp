#include "gua/godot/register_types.hpp"

#include "gua/godot/gua_context.hpp"
#include "gua/godot/spatial_host.hpp"

#include <gdextension_interface.h>

#include <godot_cpp/godot.hpp>

using namespace godot;

void initialize_gua_godot_module(ModuleInitializationLevel level)
{
    if (level != MODULE_INITIALIZATION_LEVEL_SCENE) {
        return;
    }

    GDREGISTER_CLASS(GuaContext);
    GDREGISTER_CLASS(GuaSpatialHost);
}

void uninitialize_gua_godot_module(ModuleInitializationLevel level)
{
    if (level != MODULE_INITIALIZATION_LEVEL_SCENE) {
        return;
    }
}

extern "C" {

GDExtensionBool GDE_EXPORT gua_godot_library_init(
    GDExtensionInterfaceGetProcAddress get_proc_address,
    GDExtensionClassLibraryPtr library,
    GDExtensionInitialization* initialization)
{
    godot::GDExtensionBinding::InitObject init_object(get_proc_address, library, initialization);
    init_object.register_initializer(initialize_gua_godot_module);
    init_object.register_terminator(uninitialize_gua_godot_module);
    init_object.set_minimum_library_initialization_level(MODULE_INITIALIZATION_LEVEL_SCENE);
    return init_object.init();
}

}
