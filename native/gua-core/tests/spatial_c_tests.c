#include "gua/spatial.h"
#include <assert.h>
#include <stddef.h>
int main(void) {
    gua_spatial_parse_options_v1_t o={sizeof(o),GUA_SPATIAL_REQUEST};
    gua_spatial_error_t e; gua_spatial_document_t* d=(gua_spatial_document_t*)1;
    gua_value_text_t text={"{}",2};
    assert(sizeof(o)==8 && offsetof(gua_spatial_error_t,path)==4);
    assert(gua_spatial_from_json(&o,text,&d,&e)==GUA_SPATIAL_INVALID && d==NULL);
    assert(gua_spatial_document_type(NULL)==0);
    assert(gua_spatial_copy_json(NULL,NULL,0)==0);
    gua_spatial_destroy(NULL);
    return 0;
}
