#pragma once
#include "value.h"
#include <limits>
#include <memory>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace gua {
class ValueError : public std::runtime_error {
public:
    int code;
    std::string path;
    explicit ValueError(const gua_value_error_t& e):std::runtime_error("Value error "+std::to_string(e.code)+" at "+e.path),code(e.code),path(e.path) {}
};
inline gua_value_text_t value_text(std::string_view s) {
    if(s.size()>std::numeric_limits<uint32_t>::max()) throw std::length_error("Value text too long");
    return {s.data(),static_cast<uint32_t>(s.size())};
}
template<class H,class F> inline std::string value_copy_json(H* h,F copy) {
    int n=copy(h,nullptr,0); if(n<=0) throw std::runtime_error("Value JSON copy failed");
    std::string s(static_cast<size_t>(n),'\0'); if(copy(h,s.data(),n)!=n) throw std::runtime_error("Value JSON copy failed"); s.pop_back(); return s;
}
class EnumCatalog {
    std::unique_ptr<gua_enum_catalog_t,decltype(&gua_enum_catalog_destroy)> handle_{nullptr,gua_enum_catalog_destroy};
public:
    EnumCatalog():handle_(gua_enum_catalog_create(),gua_enum_catalog_destroy) { if(!handle_) throw std::bad_alloc(); }
    explicit EnumCatalog(std::string_view json):handle_(nullptr,gua_enum_catalog_destroy) { gua_enum_catalog_t* h=nullptr; gua_value_error_t e{}; if(gua_enum_catalog_from_json(value_text(json),&h,&e)) throw ValueError(e); handle_.reset(h); }
    void register_enum(std::string_view id,const std::vector<std::string>& members) { std::vector<gua_value_text_t> m; for(const auto& s:members) m.push_back(value_text(s)); if(m.size()>UINT32_MAX) throw std::length_error("Too many enum members"); gua_value_error_t e{}; if(gua_enum_catalog_register(get(),value_text(id),m.data(),static_cast<uint32_t>(m.size()),&e)) throw ValueError(e); }
    gua_enum_catalog_t* get() const { return handle_.get(); }
    std::string to_json() const { return value_copy_json(get(),gua_enum_catalog_copy_json); }
};
class Value {
    std::unique_ptr<gua_value_t,decltype(&gua_value_destroy)> handle_{nullptr,gua_value_destroy};
public:
    explicit Value(std::string_view json,const EnumCatalog* catalog=nullptr) { gua_value_t* h=nullptr; gua_value_error_t e{}; if(gua_value_from_json(value_text(json),catalog?catalog->get():nullptr,&h,&e)) throw ValueError(e); handle_.reset(h); }
    explicit Value(const gua_value_descriptor_v1_t& d,const EnumCatalog* catalog=nullptr) { gua_value_t* h=nullptr; gua_value_error_t e{}; if(gua_value_create(&d,catalog?catalog->get():nullptr,&h,&e)) throw ValueError(e); handle_.reset(h); }
    static Value boolean(bool x) { gua_value_descriptor_v1_t d{}; d.struct_size=sizeof(d); d.type=GUA_VALUE_BOOL; d.boolean=x?1:0; return Value(d); }
    static Value integer(int64_t x) { gua_value_descriptor_v1_t d{}; d.struct_size=sizeof(d); d.type=GUA_VALUE_INTEGER; d.integer=x; return Value(d); }
    static Value number(double x) { gua_value_descriptor_v1_t d{}; d.struct_size=sizeof(d); d.type=GUA_VALUE_NUMBER; d.number=x; return Value(d); }
    static Value string(std::string_view x) { gua_value_descriptor_v1_t d{}; d.struct_size=sizeof(d); d.type=GUA_VALUE_STRING; d.text=value_text(x); return Value(d); }
    static Value enumeration(std::string_view id,std::string_view name,const EnumCatalog& catalog) { gua_value_descriptor_v1_t d{}; d.struct_size=sizeof(d); d.type=GUA_VALUE_ENUM; d.text=value_text(name); d.enum_type=value_text(id); return Value(d,&catalog); }
    static Value collection(int type,int element,const std::vector<const Value*>& values,std::string_view enum_type={},const EnumCatalog* catalog=nullptr) { std::vector<const gua_value_t*> items; for(auto v:values) items.push_back(v?v->get():nullptr); if(items.size()>UINT32_MAX) throw std::length_error("Too many elements"); gua_value_descriptor_v1_t d{}; d.struct_size=sizeof(d); d.type=type; d.element_type=element; d.enum_type=value_text(enum_type); d.items=items.data(); d.item_count=static_cast<uint32_t>(items.size()); return Value(d,catalog); }
    gua_value_t* get() const { return handle_.get(); }
    int type() const { return gua_value_get_type(get()); }
    int element_type() const { return gua_value_get_element_type(get()); }
    std::string to_json() const { return value_copy_json(get(),gua_value_copy_json); }
    std::string enum_catalog_json() const { return value_copy_json(get(),gua_value_copy_enum_catalog_json); }
    bool operator==(const Value& other) const { int result=0; gua_value_error_t e{}; if(gua_value_equals(get(),other.get(),&result,&e)) throw ValueError(e); return result!=0; }
};
}
