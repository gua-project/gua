#include "gua/spatial.hpp"
#include "gua/gua.h"
#include "../src/value_json.hpp"
#include <cassert>
#include <fstream>
#include <iostream>
#include <sstream>
#include <cstring>
using namespace gua_value_detail;
int type(const std::string& s) { return s=="request"?1:s=="result"?2:3; }
template<class F> void expect(F f,int code) {
    try { f(); assert(code==0); } catch(const gua::SpatialError& e) { if(e.code!=code) std::cerr<<"expected "<<code<<" actual "<<e.code<<std::endl; assert(e.code==code); assert(e.path.starts_with("$")); }
}
int main() {
    std::ifstream input(GUA_SPATIAL_FIXTURE); assert(input); std::stringstream stream; stream<<input.rdbuf(); auto f=parser(stream.str()).parse();
    for(const auto& c:f.at("valid").items) {
        std::cout<<"valid "<<c.at("id").text<<std::endl;
        gua::SpatialDocument d(type(c.at("type").text),c.at("json").text),r(d.type(),d.to_json()); assert(r.to_json()==d.to_json());
        const int n=gua_spatial_copy_json(d.get(),nullptr,0); char small[2]={'x','y'};
        assert(gua_spatial_copy_json(d.get(),small,2)==n&&small[0]==0&&small[1]=='y');
        assert(gua_spatial_copy_json(d.get(),nullptr,1)==0);
        std::vector<char> exact(static_cast<size_t>(n)+1,'!'); assert(gua_spatial_copy_json(d.get(),exact.data(),n)==n&&exact.back()=='!');
    }
    for(const auto& c:f.at("invalid").items) {
        std::cout<<"invalid "<<c.at("id").text<<std::endl;
        expect([&]{gua::SpatialDocument d(type(c.at("type").text),c.at("json").text);},static_cast<int>(integer(c.at("error").text,"$")));
    }
    for(const auto& c:f.at("requestChecks").items) {
        gua::SpatialDocument r(1,c.at("request").text),p(3,c.at("provider").text);
        expect([&]{r.check_request(p);},static_cast<int>(integer(c.at("error").text,"$")));
    }
    for(const auto& c:f.at("resultChecks").items) {
        gua::SpatialDocument r(1,c.at("request").text),p(2,c.at("result").text);
        expect([&]{r.check_result(p);},static_cast<int>(integer(c.at("error").text,"$")));
    }
    // Existing runtime does not advertise a provider merely because offline types exist.
    int n=gua_copy_version_json(nullptr,0); std::vector<char> version(static_cast<size_t>(n));
    gua_copy_version_json(version.data(),n); assert(std::string(version.data()).find("spatial_read_r1")==std::string::npos);
    gua_spatial_document_t* out=reinterpret_cast<gua_spatial_document_t*>(1); gua_spatial_error_t error{};
    gua_spatial_parse_options_v1_t options{sizeof(options)+4,1};
    assert(gua_spatial_from_json(&options,{nullptr,1},&out,&error)==GUA_SPATIAL_INVALID&&out==nullptr);
    assert(gua_spatial_check_request(nullptr,nullptr,&error)==GUA_SPATIAL_INVALID);
    std::cout<<"Spatial shared fixtures and ABI compatibility passed"<<std::endl;
}
