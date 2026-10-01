#include "gua/spatial_host.h"
#include <assert.h>
#include <stddef.h>
int main(void) {
    assert(sizeof(gua_spatial_host_options_v1_t)==40);
    assert(offsetof(gua_spatial_host_options_v1_t,query_deadline_ms)==24);
    gua_spatial_host_options_v1_t o={sizeof(o),1,1,1,1,1,100,100};
    gua_spatial_host_t* h=(gua_spatial_host_t*)1; gua_spatial_error_t e={0};
    assert(gua_spatial_host_create(&o,(gua_value_text_t){"clock",5},&h,&e)==0&&h);
    gua_spatial_document_t* d=(gua_spatial_document_t*)1;
    assert(gua_spatial_host_poll(h,1,1,&d,&e)==GUA_SPATIAL_STALE&&d==NULL);
    assert(gua_spatial_host_take(h,0,&d,&e)==GUA_SPATIAL_STALE&&d==NULL);
    assert(gua_spatial_host_end(h,0,&e)==GUA_SPATIAL_STALE);
    gua_spatial_host_destroy(h);
    o.struct_size--; h=(gua_spatial_host_t*)1;
    assert(gua_spatial_host_create(&o,(gua_value_text_t){"clock",5},&h,&e)==GUA_SPATIAL_INVALID&&h==NULL);
    return 0;
}
