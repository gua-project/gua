#pragma once
#include <algorithm>
// Internal JSON reader: retains number lexemes so integer validation cannot round.
#include <charconv>
#include <cmath>
#include <cstdint>
#include <map>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>
#include "gua/value.h"

namespace gua_value_detail {
struct failure { int code; std::string path; };
[[noreturn]] inline void fail(int code, std::string path = "$") { throw failure{code, std::move(path)}; }
inline bool unicode(std::string_view s) {
    for (size_t i=0; i<s.size();) {
        auto c=static_cast<unsigned char>(s[i++]);
        if(c<0x80) continue;
        int n; uint32_t cp, minimum;
        if(c>=0xc2 && c<=0xdf) { n=1; cp=c&31; minimum=0x80; }
        else if(c>=0xe0 && c<=0xef) { n=2; cp=c&15; minimum=0x800; }
        else if(c>=0xf0 && c<=0xf4) { n=3; cp=c&7; minimum=0x10000; }
        else return false;
        while(n--) { if(i==s.size()) return false; c=static_cast<unsigned char>(s[i++]); if((c&0xc0)!=0x80) return false; cp=(cp<<6)|(c&63); }
        if(cp<minimum || cp>0x10ffff || (cp>=0xd800 && cp<=0xdfff)) return false;
    }
    return true;
}
inline void append_utf8(std::string& s, uint32_t cp) {
    if(cp<0x80) s+=static_cast<char>(cp);
    else if(cp<0x800) { s+=static_cast<char>(0xc0|(cp>>6)); s+=static_cast<char>(0x80|(cp&63)); }
    else if(cp<0x10000) { s+=static_cast<char>(0xe0|(cp>>12)); s+=static_cast<char>(0x80|((cp>>6)&63)); s+=static_cast<char>(0x80|(cp&63)); }
    else { s+=static_cast<char>(0xf0|(cp>>18)); s+=static_cast<char>(0x80|((cp>>12)&63)); s+=static_cast<char>(0x80|((cp>>6)&63)); s+=static_cast<char>(0x80|(cp&63)); }
}
struct json {
    enum kind { null, boolean, number, string, array, object } type=null;
    std::string text;
    std::vector<json> items;
    std::map<std::string,json> fields;
    const json& at(const std::string& key) const { auto p=fields.find(key); if(p==fields.end()) fail(GUA_VALUE_STRUCTURE); return p->second; }
};
class parser {
    std::string_view s; size_t p=0;
    void ws() { while(p<s.size() && (s[p]==' '||s[p]=='\n'||s[p]=='\r'||s[p]=='\t')) ++p; }
    bool take(char c) { ws(); if(p<s.size() && s[p]==c) { ++p; return true; } return false; }
    uint32_t hex() { uint32_t r=0; for(int i=0;i<4;++i) { if(p==s.size()) fail(GUA_VALUE_STRUCTURE); char c=s[p++]; int n=c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:c>='A'&&c<='F'?c-'A'+10:-1; if(n<0) fail(GUA_VALUE_STRUCTURE); r=r*16+static_cast<uint32_t>(n); } return r; }
    std::string str() {
        if(!take('"')) fail(GUA_VALUE_STRUCTURE);
        std::string r;
        while(p<s.size()) {
            char c=s[p++]; if(c=='"') { if(!unicode(r)) fail(GUA_VALUE_UNICODE); return r; }
            if(static_cast<unsigned char>(c)<32) fail(GUA_VALUE_STRUCTURE);
            if(c!='\\') { r+=c; continue; }
            if(p==s.size()) fail(GUA_VALUE_STRUCTURE);
            c=s[p++];
            switch(c) {
            case '"': case '\\': case '/': r+=c; break;
            case 'b': r+='\b'; break; case 'f': r+='\f'; break; case 'n': r+='\n'; break; case 'r': r+='\r'; break; case 't': r+='\t'; break;
            case 'u': { uint32_t cp=hex(); if(cp>=0xd800&&cp<=0xdbff) { if(p+2>s.size()||s.substr(p,2)!="\\u") fail(GUA_VALUE_UNICODE); p+=2; uint32_t lo=hex(); if(lo<0xdc00||lo>0xdfff) fail(GUA_VALUE_UNICODE); cp=0x10000+((cp-0xd800)<<10)+(lo-0xdc00); } else if(cp>=0xdc00&&cp<=0xdfff) fail(GUA_VALUE_UNICODE); append_utf8(r,cp); break; }
            default: fail(GUA_VALUE_STRUCTURE);
            }
        }
        fail(GUA_VALUE_STRUCTURE);
    }
    json read(int depth) {
        if(depth>64) fail(GUA_VALUE_STRUCTURE);
        ws(); if(p==s.size()) fail(GUA_VALUE_STRUCTURE);
        json j;
        if(s[p]=='"') { j.type=json::string; j.text=str(); return j; }
        if(take('{')) { j.type=json::object; if(take('}')) return j; do { auto k=str(); if(!take(':')) fail(GUA_VALUE_STRUCTURE); auto v=read(depth+1); if(!j.fields.emplace(k,std::move(v)).second) fail(GUA_VALUE_STRUCTURE); } while(take(',')); if(!take('}')) fail(GUA_VALUE_STRUCTURE); return j; }
        if(take('[')) { j.type=json::array; if(take(']')) return j; do { j.items.push_back(read(depth+1)); } while(take(',')); if(!take(']')) fail(GUA_VALUE_STRUCTURE); return j; }
        for(auto literal:{"true","false","null"}) { std::string_view l(literal); if(s.substr(p,l.size())==l) { p+=l.size(); j.type=l=="null"?json::null:json::boolean; j.text=l; return j; } }
        size_t start=p;
        if(s[p]=='-') ++p;
        if(p==s.size()) fail(GUA_VALUE_STRUCTURE);
        if(s[p]=='0') ++p;
        else { if(s[p]<'1'||s[p]>'9') fail(GUA_VALUE_STRUCTURE); while(p<s.size()&&s[p]>='0'&&s[p]<='9') ++p; }
        if(p<s.size()&&s[p]=='.') { ++p; size_t b=p; while(p<s.size()&&s[p]>='0'&&s[p]<='9') ++p; if(b==p) fail(GUA_VALUE_STRUCTURE); }
        if(p<s.size()&&(s[p]=='e'||s[p]=='E')) { ++p; if(p<s.size()&&(s[p]=='+'||s[p]=='-')) ++p; size_t b=p; while(p<s.size()&&s[p]>='0'&&s[p]<='9') ++p; if(b==p) fail(GUA_VALUE_STRUCTURE); }
        j.type=json::number; j.text=s.substr(start,p-start); return j;
    }
public:
    explicit parser(std::string_view input):s(input) {}
    json parse() { if(!unicode(s)) fail(GUA_VALUE_UNICODE); auto j=read(0); ws(); if(p!=s.size()) fail(GUA_VALUE_STRUCTURE); return j; }
};
inline std::string quote(std::string_view s) {
    std::string r="\""; constexpr char hex[]="0123456789abcdef";
    for(unsigned char c:s) { if(c=='"'||c=='\\') { r+='\\'; r+=static_cast<char>(c); } else if(c<32) { r+="\\u00"; r+=hex[c>>4]; r+=hex[c&15]; } else r+=static_cast<char>(c); }
    return r+'"';
}
// Exact decimal integer conversion, including exponent notation. No double intermediate.
inline int64_t integer(std::string_view s, const std::string& path) {
    bool negative=s[0]=='-'; if(negative) s.remove_prefix(1);
    auto e=s.find_first_of("eE"); auto mant=s.substr(0,e); int64_t exp=0;
    if(e!=s.npos) { auto x=s.substr(e+1); bool minus=x[0]=='-'; if(x[0]=='-'||x[0]=='+') x.remove_prefix(1); const auto bound=static_cast<int64_t>(s.size())+32; for(char c:x) exp=std::min(bound,exp*10+c-'0'); if(minus) exp=-exp; }
    std::string digits; bool dot=false; for(char c:mant) { if(c=='.') dot=true; else { digits+=c; if(dot) --exp; } }
    auto first=digits.find_first_not_of('0'); if(first==digits.npos) return 0; digits.erase(0,first);
    while(!digits.empty()&&digits.back()=='0') { digits.pop_back(); ++exp; }
    if(exp<0 || exp>16 || static_cast<int64_t>(digits.size())+exp>16) fail(GUA_VALUE_RANGE,path);
    digits.append(static_cast<size_t>(exp),'0');
    if(digits.size()==16&&digits>"9007199254740991") fail(GUA_VALUE_RANGE,path);
    int64_t v=0; for(char c:digits) v=v*10+c-'0'; return negative?-v:v;
}
inline std::string number(double d) {
    if(d==0) return "0";
    char b[64]; auto r=std::to_chars(b,b+sizeof(b),d); if(r.ec!=std::errc{}) fail(GUA_VALUE_INTERNAL); return {b,r.ptr};
}
inline int64_t decimal_order(std::string_view s) {
    if(s[0]=='-') s.remove_prefix(1);
    auto e=s.find_first_of("eE"); auto mant=s.substr(0,e); int64_t exp=0;
    if(e!=s.npos) { auto x=s.substr(e+1); bool negative=x[0]=='-'; if(x[0]=='-'||x[0]=='+') x.remove_prefix(1); const auto bound=static_cast<int64_t>(s.size())+1024; for(char c:x) exp=std::min(bound,exp*10+c-'0'); if(negative) exp=-exp; }
    auto dot=mant.find('.'); int64_t before=static_cast<int64_t>(dot==mant.npos?mant.size():dot);
    int64_t index=0;
    for(char c:mant) { if(c=='.') continue; if(c!='0') return exp+before-index-1; ++index; }
    return -1000000;
}
}
