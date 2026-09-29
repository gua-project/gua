#include "gua/value.h"
#include "value_json.hpp"
#include <algorithm>
#include <cstring>
#include <limits>
#include <locale>
#include <memory>
#include <set>
#include <sstream>

using namespace gua_value_detail;
struct gua_enum_catalog_t { std::map<std::string,std::vector<std::string>> enums; };
struct gua_value_t {
    int type=0, element=0;
    bool boolean=false;
    int64_t integer=0;
    double number=0;
    std::string text, enum_type;
    std::vector<gua_value_t> items;
};
namespace {
constexpr const char* names[]={"", "bool","integer","number","string","enum","list","set"};
std::string input(gua_value_text_t s, const std::string& path="$") {
    if(!s.data&&s.size) fail(GUA_VALUE_STRUCTURE,path);
    std::string r(s.data?s.data:"",s.size); if(!unicode(r)) fail(GUA_VALUE_UNICODE,path); return r;
}
int type(const json& j, const std::string& path) {
    if(j.type!=json::string) fail(GUA_VALUE_STRUCTURE,path);
    for(int i=1;i<=7;++i) if(j.text==names[i]) return i;
    fail(GUA_VALUE_FORBIDDEN_TYPE,path);
}
void keys(const json& j, std::initializer_list<const char*> allowed) {
    if(j.type!=json::object || j.fields.size()!=allowed.size()) fail(GUA_VALUE_STRUCTURE);
    for(auto k:allowed) if(!j.fields.contains(k)) fail(GUA_VALUE_STRUCTURE);
}
bool enum_id(std::string_view s) {
    bool start=true, dot=false;
    for(char c:s) {
        if(c=='.'&&!start) { dot=true; start=true; continue; }
        bool letter=(c>='A'&&c<='Z')||(c>='a'&&c<='z')||c=='_';
        if(!letter && (start||c<'0'||c>'9')) return false;
        start=false;
    }
    return dot&&!start;
}
void register_enum(gua_enum_catalog_t& cat, const std::string& id, const std::vector<std::string>& members) {
    if(!enum_id(id)) fail(GUA_VALUE_STRUCTURE,"$.enumType");
    if(members.empty()) fail(GUA_VALUE_STRUCTURE,"$.members");
    std::set<std::string> distinct;
    for(size_t i=0;i<members.size();++i) {
        auto path="$.members["+std::to_string(i)+"]";
        if(!unicode(members[i])) fail(GUA_VALUE_UNICODE,path);
        if(members[i].empty()) fail(GUA_VALUE_STRUCTURE,path);
        if(!distinct.insert(members[i]).second) fail(GUA_VALUE_DUPLICATE,path);
    }
    auto it=cat.enums.find(id);
    if(it!=cat.enums.end()) { if(std::set<std::string>(it->second.begin(),it->second.end())!=distinct) fail(GUA_VALUE_ENUM_CONFLICT,"$.enumType"); return; }
    cat.enums.emplace(id,members);
}
void enum_check(const gua_enum_catalog_t* cat, const std::string& id, const std::string* member) {
    if(!enum_id(id)) fail(GUA_VALUE_STRUCTURE,"$.enumType");
    if(!cat || !cat->enums.contains(id)) fail(GUA_VALUE_ENUM_UNKNOWN,"$.enumType");
    const auto& candidates=cat->enums.at(id);
    if(member&&std::find(candidates.begin(),candidates.end(),*member)==candidates.end()) fail(GUA_VALUE_ENUM_MEMBER,"$.value");
}
bool equal(const gua_value_t& a,const gua_value_t& b) {
    if(a.type!=b.type||a.element!=b.element||a.enum_type!=b.enum_type) return false;
    switch(a.type) {
    case GUA_VALUE_BOOL:return a.boolean==b.boolean;
    case GUA_VALUE_INTEGER:return a.integer==b.integer;
    case GUA_VALUE_NUMBER:return a.number==b.number;
    case GUA_VALUE_STRING:case GUA_VALUE_ENUM:return a.text==b.text;
    default:
        if(a.items.size()!=b.items.size()) return false;
        for(size_t i=0;i<a.items.size();++i) {
            if(a.type==GUA_VALUE_LIST) { if(!equal(a.items[i],b.items[i])) return false; }
            else if(std::none_of(b.items.begin(),b.items.end(),[&](const auto& item){ return equal(a.items[i],item); })) return false;
        }
        return true;
    }
}
void add_item(gua_value_t& v, gua_value_t item, size_t index) {
    auto path="$.value["+std::to_string(index)+"]";
    if(item.type!=v.element||item.enum_type!=v.enum_type) fail(GUA_VALUE_ELEMENT_TYPE,path);
    if(v.type==GUA_VALUE_SET&&std::any_of(v.items.begin(),v.items.end(),[&](const auto& x){return equal(x,item);})) fail(GUA_VALUE_DUPLICATE,path);
    v.items.push_back(std::move(item));
}
gua_value_t scalar(int t,const json& j,const std::string& id,const gua_enum_catalog_t* cat,const std::string& path) {
    gua_value_t v; v.type=t; v.enum_type=id;
    if(j.type==json::null||j.type==json::object||j.type==json::array) fail(GUA_VALUE_FORBIDDEN_TYPE,path);
    switch(t) {
    case GUA_VALUE_BOOL: if(j.type!=json::boolean) fail(GUA_VALUE_ELEMENT_TYPE,path); v.boolean=j.text=="true"; break;
    case GUA_VALUE_INTEGER: if(j.type!=json::number) fail(GUA_VALUE_ELEMENT_TYPE,path); v.integer=gua_value_detail::integer(j.text,path); break;
    case GUA_VALUE_NUMBER: {
        if(j.type!=json::number) fail(GUA_VALUE_ELEMENT_TYPE,path);
        // Xcode 16 libc++ has no floating-point from_chars overload. The JSON
        // parser already checked the token grammar; classic locale keeps the
        // portable conversion independent of the application's decimal point.
        std::istringstream stream(j.text);
        stream.imbue(std::locale::classic());
        stream >> v.number;
        // Some libraries mark underflow with failbit even when they return a
        // rounded subnormal. Preserve that result; reject overflow/saturation.
        const bool underflow=decimal_order(j.text)<=-308 &&
            std::abs(v.number)<std::numeric_limits<double>::min();
        if(!std::isfinite(v.number) || (stream.fail() && !underflow))
            fail(GUA_VALUE_NON_FINITE,path);
        break;
    }
    case GUA_VALUE_STRING:case GUA_VALUE_ENUM:
        if(j.type!=json::string) fail(GUA_VALUE_ELEMENT_TYPE,path);
        v.text=j.text;
        if(t==GUA_VALUE_ENUM) { try { enum_check(cat,id,&v.text); } catch(failure& e) { if(e.code==GUA_VALUE_ENUM_MEMBER) e.path=path; throw; } }
        break;
    default: fail(GUA_VALUE_FORBIDDEN_TYPE,path);
    }
    return v;
}
gua_value_t parse_value(const json& j,const gua_enum_catalog_t* cat) {
    int t=type(j.at("type"),"$.type"); const auto& raw=j.at("value");
    bool collection=t==GUA_VALUE_LIST||t==GUA_VALUE_SET;
    int el=collection?type(j.at("elementType"),"$.elementType"):0;
    if(collection&&el>GUA_VALUE_ENUM) fail(GUA_VALUE_FORBIDDEN_TYPE,"$.elementType");
    bool en=t==GUA_VALUE_ENUM||el==GUA_VALUE_ENUM; std::string id;
    if(en) { const auto& e=j.at("enumType"); if(e.type!=json::string) fail(GUA_VALUE_STRUCTURE,"$.enumType"); id=e.text; enum_check(cat,id,nullptr); }
    if(collection) { if(en) keys(j,{"type","value","elementType","enumType"}); else keys(j,{"type","value","elementType"}); }
    else { if(en) keys(j,{"type","value","enumType"}); else keys(j,{"type","value"}); }
    if(!collection) return scalar(t,raw,id,cat,"$.value");
    if(raw.type!=json::array) fail(GUA_VALUE_STRUCTURE,"$.value");
    gua_value_t v; v.type=t; v.element=el; v.enum_type=id;
    for(size_t i=0;i<raw.items.size();++i) add_item(v,scalar(el,raw.items[i],id,cat,"$.value["+std::to_string(i)+"]"),i);
    return v;
}
std::string payload(const gua_value_t& v) {
    switch(v.type) {
    case GUA_VALUE_BOOL:return v.boolean?"true":"false";
    case GUA_VALUE_INTEGER:return std::to_string(v.integer);
    case GUA_VALUE_NUMBER:return gua_value_detail::number(v.number);
    case GUA_VALUE_STRING:case GUA_VALUE_ENUM:return quote(v.text);
    default: { std::string s="["; for(const auto& x:v.items) { if(s.size()>1) s+=','; s+=payload(x); } return s+"]"; }
    }
}
std::string serialize(const gua_value_t& v) {
    std::string s="{\"type\":"+quote(names[v.type]);
    if(v.element) s+=",\"elementType\":"+quote(names[v.element]);
    if(!v.enum_type.empty()) s+=",\"enumType\":"+quote(v.enum_type);
    return s+",\"value\":"+payload(v)+"}";
}
int copy(const std::string& s,char* out,int cap) {
    if(cap<0 || (!out&&cap!=0) || s.size()>=static_cast<size_t>(std::numeric_limits<int>::max())) return 0;
    int size=static_cast<int>(s.size()+1);
    if(out&&cap>0) { out[0]=0; if(cap>=size) std::memcpy(out,s.c_str(),static_cast<size_t>(size)); }
    return size;
}
template<class F> int protect(gua_value_error_t* error,F f) noexcept {
    if(error) *error={};
    try { f(); return GUA_VALUE_OK; }
    catch(const failure& e) { if(error) { error->code=e.code; auto n=std::min(e.path.size(),sizeof(error->path)-1); std::memcpy(error->path,e.path.data(),n); error->path[n]=0; } return e.code; }
    catch(...) { if(error) { error->code=GUA_VALUE_INTERNAL; error->path[0]='$'; error->path[1]=0; } return GUA_VALUE_INTERNAL; }
}
}
extern "C" {
gua_enum_catalog_t* gua_enum_catalog_create() { try { return new gua_enum_catalog_t; } catch(...) { return nullptr; } }
void gua_enum_catalog_destroy(gua_enum_catalog_t* c) { delete c; }
int gua_enum_catalog_register(gua_enum_catalog_t* c,gua_value_text_t id,const gua_value_text_t* members,uint32_t count,gua_value_error_t* error) {
    return protect(error,[&] { if(!c||(!members&&count)) fail(GUA_VALUE_STRUCTURE); std::vector<std::string> m; for(uint32_t i=0;i<count;++i) m.push_back(input(members[i],"$.members["+std::to_string(i)+"]")); register_enum(*c,input(id,"$.enumType"),m); });
}
int gua_enum_catalog_validate_type(const gua_enum_catalog_t* c,gua_value_text_t id,gua_value_error_t* error) {
    return protect(error,[&] { if(!c) fail(GUA_VALUE_STRUCTURE); enum_check(c,input(id,"$.enumType"),nullptr); });
}
int gua_enum_catalog_from_json(gua_value_text_t s,gua_enum_catalog_t** out,gua_value_error_t* error) {
    if(out) *out=nullptr;
    return protect(error,[&] {
        if(!out) fail(GUA_VALUE_STRUCTURE);
        auto j=parser(input(s)).parse(); keys(j,{"schemaVersion","enums"});
        const auto& version=j.at("schemaVersion");
        if(version.type!=json::number||gua_value_detail::integer(version.text,"$.schemaVersion")!=1) fail(GUA_VALUE_STRUCTURE,"$.schemaVersion");
        const auto& list=j.at("enums"); if(list.type!=json::array) fail(GUA_VALUE_STRUCTURE,"$.enums");
        auto c=std::make_unique<gua_enum_catalog_t>();
        for(const auto& e:list.items) { keys(e,{"enumType","members"}); if(e.at("enumType").type!=json::string||e.at("members").type!=json::array) fail(GUA_VALUE_STRUCTURE); std::vector<std::string> m; for(const auto& x:e.at("members").items) { if(x.type!=json::string) fail(GUA_VALUE_STRUCTURE,"$.members"); m.push_back(x.text); } register_enum(*c,e.at("enumType").text,m); }
        *out=c.release();
    });
}
int gua_enum_catalog_copy_json(const gua_enum_catalog_t* c,char* out,int cap) {
    try { if(!c) return 0; std::string s="{\"schemaVersion\":1,\"enums\":["; bool first=true; for(const auto& [id,m]:c->enums) { if(!first) s+=','; first=false; s+="{\"enumType\":"+quote(id)+",\"members\":["; for(size_t i=0;i<m.size();++i) { if(i) s+=','; s+=quote(m[i]); } s+="]}"; } return copy(s+"]}",out,cap); } catch(...) { return 0; }
}
int gua_value_from_json(gua_value_text_t s,const gua_enum_catalog_t* cat,gua_value_t** out,gua_value_error_t* error) {
    if(out) *out=nullptr;
    return protect(error,[&] { if(!out) fail(GUA_VALUE_STRUCTURE); auto j=parser(input(s)).parse(); auto v=std::make_unique<gua_value_t>(parse_value(j,cat)); *out=v.release(); });
}
int gua_value_create(const gua_value_descriptor_v1_t* d,const gua_enum_catalog_t* cat,gua_value_t** out,gua_value_error_t* error) {
    if(out) *out=nullptr;
    return protect(error,[&] {
        if(!out||!d||d->struct_size<sizeof(*d)) fail(GUA_VALUE_STRUCTURE);
        if(d->type<1||d->type>7) fail(GUA_VALUE_FORBIDDEN_TYPE,"$.type");
        auto v=std::make_unique<gua_value_t>(); v->type=d->type;
        bool collection=d->type>=GUA_VALUE_LIST, en=d->type==GUA_VALUE_ENUM||(collection&&d->element_type==GUA_VALUE_ENUM);
        if(!collection&&(d->element_type||d->item_count||d->items)) fail(GUA_VALUE_STRUCTURE);
        if(!en&&d->enum_type.size) fail(GUA_VALUE_STRUCTURE,"$.enumType");
        if(en) { v->enum_type=input(d->enum_type,"$.enumType"); enum_check(cat,v->enum_type,nullptr); }
        switch(d->type) {
        case GUA_VALUE_BOOL:if(d->boolean!=0&&d->boolean!=1) fail(GUA_VALUE_ELEMENT_TYPE,"$.value"); v->boolean=d->boolean!=0; break;
        case GUA_VALUE_INTEGER:if(d->integer < -9007199254740991LL||d->integer>9007199254740991LL) fail(GUA_VALUE_RANGE,"$.value"); v->integer=d->integer; break;
        case GUA_VALUE_NUMBER:if(!std::isfinite(d->number)) fail(GUA_VALUE_NON_FINITE,"$.value"); v->number=d->number; break;
        case GUA_VALUE_STRING:case GUA_VALUE_ENUM:v->text=input(d->text,"$.value"); if(en) enum_check(cat,v->enum_type,&v->text); break;
        default:
            v->element=d->element_type; if(v->element<1||v->element>5) fail(GUA_VALUE_FORBIDDEN_TYPE,"$.elementType");
            if(!d->items&&d->item_count) fail(GUA_VALUE_STRUCTURE,"$.value");
            for(uint32_t i=0;i<d->item_count;++i) { if(!d->items[i]) fail(GUA_VALUE_STRUCTURE,"$.value["+std::to_string(i)+"]"); if(en&&d->items[i]->type==GUA_VALUE_ENUM) enum_check(cat,v->enum_type,&d->items[i]->text); add_item(*v,*d->items[i],i); }
        }
        *out=v.release();
    });
}
void gua_value_destroy(gua_value_t* v) { delete v; }
int gua_value_get_type(const gua_value_t* v) { return v?v->type:0; }
int gua_value_get_element_type(const gua_value_t* v) { return v?v->element:0; }
int gua_value_copy_json(const gua_value_t* v,char* out,int cap) { try { return v?copy(serialize(*v),out,cap):0; } catch(...) { return 0; } }
int gua_value_equals(const gua_value_t* a,const gua_value_t* b,int* result,gua_value_error_t* error) { if(result) *result=0; return protect(error,[&] { if(!a||!b||!result) fail(GUA_VALUE_STRUCTURE); *result=equal(*a,*b)?1:0; }); }
}
