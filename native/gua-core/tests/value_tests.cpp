#include "gua/value.hpp"
#include "../src/value_json.hpp"
#include <cassert>
#include <fstream>
#include <iostream>
#include <limits>
#include <sstream>
#include <cstring>

using namespace gua_value_detail;
int code(const std::string& c) {
    const char* codes[]={"ok","structure","forbidden_type","range","non_finite","unicode","enum_unknown","enum_conflict","enum_member","element_type","duplicate","internal"};
    for(int i=0;i<12;++i) if(c==codes[i]) return i;
    assert(false); return -1;
}
template<class F> void rejects(F f,int expected) { bool caught=false; try { f(); } catch(const gua::ValueError& e) { caught=true; if(e.code!=expected) std::cerr<<e.what()<<" expected "<<expected<<'\n'; assert(e.code==expected); assert(e.path.starts_with("$")); assert(std::string(e.what()).find("DO_NOT_LOG")==std::string::npos); } assert(caught); }
std::string dump(const json& j) {
    if(j.type==json::string) return quote(j.text);
    if(j.type==json::number||j.type==json::boolean) return j.text;
    if(j.type==json::null) return "null";
    std::string s=j.type==json::array?"[":"{"; bool first=true;
    if(j.type==json::array) for(auto& x:j.items) { if(!first) s+=','; first=false; s+=dump(x); }
    else for(auto& [k,v]:j.fields) { if(!first) s+=','; first=false; s+=quote(k)+":"+dump(v); }
    return s+(j.type==json::array?"]":"}");
}
int main() {
    std::ifstream input(GUA_VALUE_FIXTURE); assert(input); std::stringstream stream; stream<<input.rdbuf(); auto f=parser(stream.str()).parse();
    gua::EnumCatalog catalog(dump(f.at("catalog")));
    size_t cases=0;
    for(const auto& c:f.at("valid").items) { std::cout<<"roundtrip "<<c.at("id").text<<std::endl; gua::Value v(c.at("json").text,&catalog), roundtrip(v.to_json(),&catalog); assert(v==roundtrip); ++cases; }
    for(const auto& c:f.at("invalid").items) { std::cout<<"reject "<<c.at("id").text<<std::endl; rejects([&]{gua::Value v(c.at("json").text,&catalog);},code(c.at("error").text)); ++cases; }
    for(const auto& c:f.at("comparisons").items) { gua::Value a(c.at("left").text,&catalog),b(c.at("right").text,&catalog); assert((a==b)==(c.at("equal").text=="true")); ++cases; }
    for(const auto& c:f.at("generators").items) { auto g=c.at("generate").text; if(g=="BigInt") { /* C ABI has no BigInt representation: unknown tag must be rejected. */ gua_value_descriptor_v1_t d{}; d.struct_size=sizeof(d); d.type=99; rejects([&]{gua::Value v(d);},code(c.at("error").text)); } else { double n=g=="NaN"?std::numeric_limits<double>::quiet_NaN():g=="Infinity"?std::numeric_limits<double>::infinity():-std::numeric_limits<double>::infinity(); rejects([&]{auto v=gua::Value::number(n);},code(c.at("error").text)); } ++cases; }
    catalog.register_enum("game.BossPhase",{"FirstSecond","AliasSecond","Second","First"});
    rejects([&]{catalog.register_enum("game.BossPhase",{"Third"});},GUA_VALUE_ENUM_CONFLICT);
    rejects([&]{catalog.register_enum("game.Other",{"A","A"});},GUA_VALUE_DUPLICATE);
    gua::EnumCatalog copied(catalog.to_json()); assert(copied.to_json()==catalog.to_json());
    rejects([&]{auto v=gua::Value::string(std::string("\xc0\xaf",2));},GUA_VALUE_UNICODE);
    rejects([&]{auto v=gua::Value::integer(INT64_MAX);},GUA_VALUE_RANGE);
    auto a=gua::Value::integer(1); auto b=gua::Value::integer(2);
    auto list=gua::Value::collection(GUA_VALUE_LIST,GUA_VALUE_INTEGER,{&a,&b});
    assert(list.type()==GUA_VALUE_LIST&&list.element_type()==GUA_VALUE_INTEGER);
    rejects([&]{auto v=gua::Value::collection(GUA_VALUE_SET,GUA_VALUE_INTEGER,{&a,&a});},GUA_VALUE_DUPLICATE);
    std::string source("a\0b",3); auto text=gua::Value::string(source); source[0]='x'; assert(text.to_json().find("a\\u0000b")!=std::string::npos);
    auto zero=gua::Value::number(-0.0); assert(zero.to_json().find("-0")==std::string::npos);
    const auto required=gua_value_copy_json(text.get(),nullptr,0); char short_buffer[2]={'x','y'};
    assert(gua_value_copy_json(text.get(),short_buffer,2)==required&&short_buffer[0]==0&&short_buffer[1]=='y');
    assert(gua_value_copy_json(text.get(),nullptr,1)==0);
    std::vector<char> exact(static_cast<size_t>(required)+1,'!'); assert(gua_value_copy_json(text.get(),exact.data(),required)==required&&exact.back()=='!');
    gua_value_t* out=reinterpret_cast<gua_value_t*>(1); gua_value_error_t error{};
    assert(gua_enum_catalog_validate_type(catalog.get(),gua::value_text("bad"),&error)==GUA_VALUE_STRUCTURE);
    assert(std::string(error.path)=="$.enumType");
    assert(gua_enum_catalog_validate_type(catalog.get(),gua::value_text("game.Missing"),&error)==GUA_VALUE_ENUM_UNKNOWN);
    assert(gua_enum_catalog_validate_type(catalog.get(),gua::value_text("game.BossPhase"),&error)==GUA_VALUE_OK);
    assert(gua_value_from_json({nullptr,1},nullptr,&out,&error)==GUA_VALUE_STRUCTURE&&out==nullptr);
    int eq=9; assert(gua_value_equals(nullptr,text.get(),&eq,&error)==GUA_VALUE_STRUCTURE&&eq==0);
    gua_value_destroy(nullptr); gua_enum_catalog_destroy(nullptr);
    auto preserved=[] { auto local=gua::Value::string("owned"); return gua::Value::collection(GUA_VALUE_LIST,GUA_VALUE_STRING,{&local}); }();
    assert(preserved.to_json().find("owned")!=std::string::npos);
    std::cout<<cases<<" shared cases and ABI checks passed\n";
}
