#include "gua/observe.h"
#include <assert.h>
int main(void) {
    gua_context_t* c=gua_create_context(); uint64_t owner=0,id=0;
    gua_value_text_t empty={0,0},name={"phase",5};
    assert(gua_observe_create_owner(c,GUA_OBSERVE_WORLD,empty,&owner)==0);
    gua_observe_registration_v1_t d={0};d.struct_size=sizeof(d);d.owner_id=owner;d.name=name;
    assert(gua_observe_register_v1(c,&d,&id)==0);
    assert(gua_observe_publish(c,id,0,GUA_OBSERVE_GETTER_FAILED,0)==0);
    gua_observe_result_t* r=0;assert(gua_observe_snapshot(c,0,&r)==0);
    gua_destroy_context(c); /* detached Snapshot remains owned */
    assert(gua_observe_result_copy_json(r,0,0)>0);gua_observe_result_destroy(r);
    return 0;
}
