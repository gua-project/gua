#include "gua/value.h"
#include <assert.h>
#include <string.h>

int main(void) {
    gua_value_descriptor_v1_t descriptor = {0};
    gua_value_error_t error = {0};
    gua_value_t* value = 0;
    gua_value_t* roundtrip = 0;
    char output[128];
    int equal = 0;
    descriptor.struct_size = sizeof(descriptor);
    descriptor.type = GUA_VALUE_INTEGER;
    descriptor.integer = 42;
    assert(gua_value_create(&descriptor, 0, &value, &error) == GUA_VALUE_OK);
    assert(gua_value_get_type(value) == GUA_VALUE_INTEGER);
    assert(gua_value_copy_json(value, output, sizeof(output)) > 0);
    {
        gua_value_text_t json = {output, (uint32_t)strlen(output)};
        assert(gua_value_from_json(json, 0, &roundtrip, &error) == GUA_VALUE_OK);
    }
    assert(gua_value_equals(value, roundtrip, &equal, &error) == GUA_VALUE_OK && equal);
    gua_value_destroy(roundtrip);
    gua_value_destroy(value);
    return 0;
}
