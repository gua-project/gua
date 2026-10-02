#include "gua/spatial_client.hpp"
#include "../../gua-core/src/value_json.hpp"
#include <cassert>
#include <fstream>
#include <sstream>
std::string wire(const gua_value_detail::json& j) {
    using gua_value_detail::json;
    if(j.type==json::string) return gua_value_detail::quote(j.text);
    if(j.type==json::number||j.type==json::boolean) return j.text;
    if(j.type==json::null) return "null";
    std::string out=j.type==json::array?"[":"{"; bool first=true;
    if(j.type==json::array) for(const auto& v:j.items) {if(!first) out+=','; first=false; out+=wire(v);}
    else for(const auto& [k,v]:j.fields) {if(!first) out+=','; first=false; out+=gua_value_detail::quote(k)+":"+wire(v);}
    return out+(j.type==json::array?"]":"}");
}
int main() {
    std::ifstream input(GUA_SPATIAL_HOST_FIXTURE); std::stringstream buffer; buffer<<input.rdbuf();
    const auto fixture=gua_value_detail::parser(buffer.str()).parse().at("valid").items;
    gua::SpatialDocument registration(7,wire(fixture[0].at("json"))),grants(8,wire(fixture[1].at("json"))),batch(5,wire(fixture[2].at("json"))),boundary(9,wire(fixture[3].at("json")));
    gua_spatial_host_t* host=nullptr; gua_spatial_error_t e{};
    gua_spatial_host_options_v1_t options{sizeof(options),2,4,4,64,2,1000,1000};
    assert(gua_spatial_host_create(&options,{"cpp",3},&host,&e)==0);
    uint64_t provider=0; assert(gua_spatial_host_register(host,registration.get(),&provider,&e)==0);
    auto runtime=gua_runtime_create();
    assert(gua_runtime_bind_spatial(runtime,host,provider,grants.get())==0);
    {
        gua::SpatialClient client(runtime); auto info=client.describe(); assert(info.type()==GUA_SPATIAL_ADVERTISEMENT);
        client.enqueue(batch); assert(!client.poll(1));
        uint64_t lease=0; assert(gua_spatial_host_begin(host,provider,boundary.get(),&lease,&e)==0);
        gua_spatial_document_t* request=nullptr; assert(gua_spatial_host_take(host,lease,&request,&e)==0); gua_spatial_destroy(request);
        gua_reset_options_t reset{sizeof(reset),GUA_RESET_DEFAULT_V3,0,1,GUA_RESET_FLAGS_VERSION_CURRENT};
        gua_reset_report_t report{sizeof(report)};
        assert(gua_runtime_reset_context(runtime,&reset,&report)==GUA_RESET_SUCCEEDED);
        // Reset must revoke before an adapter can perform any remaining query.
        assert(gua_spatial_host_take(host,lease,&request,&e)!=0 && request==nullptr);
        assert(gua_spatial_host_end(host,lease,&e)==0);
        bool denied=false; try { client.poll(1); } catch(const gua::SpatialError& error) {denied=error.code==GUA_SPATIAL_NOT_AUTHORIZED;}
        assert(denied);
    }
    gua_spatial_host_destroy(host); // Runtime retains its shared scheduler.
    gua_runtime_destroy(runtime);
}
