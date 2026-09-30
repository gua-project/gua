#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

/* Value v1 is independent of runtime contexts and existing World/Input values.
 * Handles own copies of all inputs. Immutable values may be read concurrently;
 * callers synchronize catalog mutation and destruction with all other uses. */
typedef struct gua_value_t gua_value_t;
typedef struct gua_enum_catalog_t gua_enum_catalog_t;
typedef struct gua_value_text_t { const char* data; uint32_t size; } gua_value_text_t;
enum {
    GUA_VALUE_BOOL = 1, GUA_VALUE_INTEGER = 2, GUA_VALUE_NUMBER = 3,
    GUA_VALUE_STRING = 4, GUA_VALUE_ENUM = 5, GUA_VALUE_LIST = 6, GUA_VALUE_SET = 7
};
enum {
    GUA_VALUE_OK = 0, GUA_VALUE_STRUCTURE = 1, GUA_VALUE_FORBIDDEN_TYPE = 2,
    GUA_VALUE_RANGE = 3, GUA_VALUE_NON_FINITE = 4, GUA_VALUE_UNICODE = 5,
    GUA_VALUE_ENUM_UNKNOWN = 6, GUA_VALUE_ENUM_CONFLICT = 7,
    GUA_VALUE_ENUM_MEMBER = 8, GUA_VALUE_ELEMENT_TYPE = 9,
    GUA_VALUE_DUPLICATE = 10, GUA_VALUE_INTERNAL = 11
};
/* Paths only refer to protocol fields/indices, never to submitted values. */
typedef struct gua_value_error_t { int32_t code; char path[128]; } gua_value_error_t;
typedef struct gua_value_descriptor_v1_t {
    uint32_t struct_size;
    int32_t type;
    int32_t element_type;
    int32_t boolean;
    int64_t integer;
    double number;
    gua_value_text_t text;
    gua_value_text_t enum_type;
    const gua_value_t* const* items;
    uint32_t item_count;
} gua_value_descriptor_v1_t;

gua_enum_catalog_t* gua_enum_catalog_create(void);
void gua_enum_catalog_destroy(gua_enum_catalog_t* catalog);
int gua_enum_catalog_register(gua_enum_catalog_t* catalog, gua_value_text_t enum_type,
    const gua_value_text_t* members, uint32_t count, gua_value_error_t* error);
/* Validate Unicode, the namespaced ID syntax, and presence in this catalog. */
int gua_enum_catalog_validate_type(const gua_enum_catalog_t* catalog, gua_value_text_t enum_type,
    gua_value_error_t* error);
/* Catalog JSON is {"schemaVersion":1,"enums":[{"enumType":...,"members":[...]}]}.
 * Parsing is atomic: on failure *out_catalog is NULL. */
int gua_enum_catalog_from_json(gua_value_text_t json, gua_enum_catalog_t** out_catalog, gua_value_error_t* error);
int gua_enum_catalog_copy_json(const gua_enum_catalog_t* catalog, char* buffer, int capacity);
int gua_value_create(const gua_value_descriptor_v1_t* descriptor, const gua_enum_catalog_t* catalog,
    gua_value_t** out_value, gua_value_error_t* error);
int gua_value_from_json(gua_value_text_t json, const gua_enum_catalog_t* catalog,
    gua_value_t** out_value, gua_value_error_t* error);
/* Deep copy, independent of the source lifetime. NULL on allocation failure. */
gua_value_t* gua_value_clone(const gua_value_t* value);
void gua_value_destroy(gua_value_t* value);
int gua_value_get_type(const gua_value_t* value);
int gua_value_get_element_type(const gua_value_t* value);
/* Returns bytes required INCLUDING NUL; zero means invalid arguments/failure.
 * A short buffer is cleared, never filled with truncated JSON. NULL/0 sizes it. */
int gua_value_copy_json(const gua_value_t* value, char* buffer, int capacity);
/* Owned candidate definition captured at construction; only the used enumType.
 * Non-enum Values return an empty catalog. Source catalog may already be freed. */
int gua_value_copy_enum_catalog_json(const gua_value_t* value, char* buffer, int capacity);
/* Returns status; unequal valid types are OK with *equal = 0. */
int gua_value_equals(const gua_value_t* left, const gua_value_t* right, int* equal, gua_value_error_t* error);
#ifdef __cplusplus
}
#endif
