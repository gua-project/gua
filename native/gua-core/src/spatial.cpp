#include "gua/spatial.h"
#include "value_json.hpp"
#include <array>
#include <cstring>
#include <limits>
#include <memory>
#include <set>
#include <locale>
#include <sstream>

using gua_value_detail::json;
struct gua_spatial_document_t { int type; json value; std::string wire; };
namespace {
using gua_value_detail::fail;
constexpr double basis_tolerance=1e-6;
void require(bool ok, int code=GUA_SPATIAL_INVALID, const char* path="$") { if(!ok) fail(code,path); }
bool has(const json& j, const char* key) { return j.fields.contains(key); }
void object(const json& j, std::initializer_list<const char*> required, std::initializer_list<const char*> optional={}) {
    require(j.type==json::object);
    for(auto k:required) require(has(j,k));
    for(const auto& [k,v]:j.fields) {
        (void)v;
        bool found=false; for(auto a:required) found|=k==a; for(auto a:optional) found|=k==a;
        require(found);
    }
}
std::string text(const json& j) { require(j.type==json::string&&!j.text.empty()); return j.text; }
std::string text(const json& j,const char* key) { return text(j.at(key)); }
std::string tag(const json& j,std::initializer_list<const char*> tags) {
    auto s=text(j); for(auto t:tags) if(s==t) return s; fail(GUA_SPATIAL_INVALID);
}
double num(const json& j) {
    require(j.type==json::number);
    // libc++ on supported Xcode versions has no floating-point from_chars.
    double n=0; std::istringstream stream(j.text); stream.imbue(std::locale::classic()); stream>>n;
    const bool underflow=gua_value_detail::decimal_order(j.text)<=-308&&std::abs(n)<std::numeric_limits<double>::min();
    require(std::isfinite(n)&&(!stream.fail()||underflow),GUA_SPATIAL_GEOMETRY);
    return n;
}
double range(const json& j,double min,double max=std::numeric_limits<double>::max()) {
    auto n=num(j); require(n>=min&&n<=max,GUA_SPATIAL_GEOMETRY); return n;
}
double positive(const json& j) { auto n=num(j); require(n>0,GUA_SPATIAL_GEOMETRY); return n; }
int64_t sequence(const json& j,bool positive=false) {
    require(j.type==json::number);
    int64_t n=0; try { n=gua_value_detail::integer(j.text,"$"); } catch(...) { fail(GUA_SPATIAL_INVALID); }
    require(n>=(positive?1:0)); return n;
}
bool boolean(const json& j) { require(j.type==json::boolean); return j.text=="true"; }
using vec=std::array<double,3>;
vec vector(const json& j) { object(j,{"x","y","z"}); return {num(j.at("x")),num(j.at("y")),num(j.at("z"))}; }
double dot(vec a,vec b) { return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]; }
void unit(vec a) { require(std::abs(dot(a,a)-1)<=basis_tolerance,GUA_SPATIAL_GEOMETRY); }
void basis(const json& j) {
    object(j,{"x","y","z"}); auto x=vector(j.at("x")),y=vector(j.at("y")),z=vector(j.at("z"));
    unit(x); unit(y); unit(z);
    require(std::abs(dot(x,y))<=basis_tolerance&&std::abs(dot(x,z))<=basis_tolerance&&std::abs(dot(y,z))<=basis_tolerance,GUA_SPATIAL_GEOMETRY);
    // Both handedness conventions are valid; no coordinate conversion is implied.
}
bool zero(vec a) { return a[0]==0&&a[1]==0&&a[2]==0; }
double length(vec a) { auto n=std::hypot(a[0],a[1],a[2]); require(std::isfinite(n),GUA_SPATIAL_GEOMETRY); return n; }
std::string shape(const json& j) {
    require(j.type==json::object); auto s=tag(j.at("type"),{"sphere","capsule","box"});
    if(s=="sphere") { object(j,{"type","center","radius"}); vector(j.at("center")); positive(j.at("radius")); }
    else if(s=="capsule") { object(j,{"type","pointA","pointB","radius"}); vector(j.at("pointA")); vector(j.at("pointB")); positive(j.at("radius")); }
    else { object(j,{"type","center","halfExtents","basis"}); vector(j.at("center"));
        for(auto d:vector(j.at("halfExtents"))) require(d>0,GUA_SPATIAL_GEOMETRY); basis(j.at("basis")); }
    return s;
}
void correlation(const json& j) {
    sequence(j.at("requestId"),true); sequence(j.at("sessionEpoch"),true);
    text(j,"queryId"); text(j,"spaceId"); sequence(j.at("spaceEpoch"),true);
    tag(j.at("kind"),{"raycast","overlap","sweep"});
}
void request(const json& j) {
    object(j,{"schemaVersion","documentType","requestId","sessionEpoch","queryId","spaceId","spaceEpoch","queryPolicyId","kind","deadlineMs","consistency"},{"segment","shape","delta","maxHits"});
    correlation(j); text(j,"queryPolicyId"); positive(j.at("deadlineMs"));
    tag(j.at("consistency"),{"bestEffort","samePhysicsSample"});
    if(has(j,"maxHits")) { auto n=sequence(j.at("maxHits"),true); require(n<=32); }
    auto k=text(j,"kind");
    if(k=="raycast") {
        require(has(j,"segment")&&!has(j,"shape")&&!has(j,"delta"));
        const auto& segment=j.at("segment"); object(segment,{"from","to"});
        auto a=vector(segment.at("from")),b=vector(segment.at("to")); vec d{b[0]-a[0],b[1]-a[1],b[2]-a[2]};
        require(!zero(d),GUA_SPATIAL_GEOMETRY); length(d);
    } else {
        require(has(j,"shape")&&!has(j,"segment")); shape(j.at("shape"));
        require(has(j,"delta")== (k=="sweep")); if(k=="sweep") length(vector(j.at("delta")));
    }
}
void region(const json& j) {
    object(j,{"min","max"}); auto a=vector(j.at("min")),b=vector(j.at("max"));
    for(size_t i=0;i<3;++i) require(a[i]<=b[i],GUA_SPATIAL_GEOMETRY);
}
void coverage(const json& j) {
    object(j,{"state"},{"loadedRegion","reason"}); auto state=tag(j.at("state"),{"complete","partial","unknown"});
    if(has(j,"loadedRegion")) region(j.at("loadedRegion"));
    if(state=="complete") require(has(j,"loadedRegion"),GUA_SPATIAL_SEMANTICS);
    else require(has(j,"reason"),GUA_SPATIAL_SEMANTICS);
    if(has(j,"reason")) text(j,"reason");
}
void sample(const json& j) {
    object(j,{"physicsSampleId","tick","observedAtMs"},{"worldSnapshot"}); text(j,"physicsSampleId"); sequence(j.at("tick")); range(j.at("observedAtMs"),0);
    if(has(j,"worldSnapshot")) {
        const auto& w=j.at("worldSnapshot"); object(w,{"sessionEpoch","frameSequence","revision"});
        sequence(w.at("sessionEpoch"),true); sequence(w.at("frameSequence")); sequence(w.at("revision"));
    }
}
void missing(const json& j) {
    object(j,{}, {"position","distance","normal","collisionRef","worldObjectId"});
    for(const auto& [k,v]:j.fields) { (void)k; text(v); }
}
void hit(const json& j) {
    object(j,{"relation","missing"},{"position","distance","normal","collisionRef","worldObjectId"});
    tag(j.at("relation"),{"contact","penetration","unknown"}); missing(j.at("missing"));
    if(has(j,"position")) vector(j.at("position"));
    if(has(j,"distance")) range(j.at("distance"),0);
    if(has(j,"normal")) unit(vector(j.at("normal")));
    if(has(j,"collisionRef")) text(j,"collisionRef"); if(has(j,"worldObjectId")) text(j,"worldObjectId");
    for(auto k:{"position","distance","normal","collisionRef","worldObjectId"})
        require(has(j,k)!=has(j.at("missing"),k),GUA_SPATIAL_SEMANTICS);
}
void motion(const json& j) {
    require(j.type==json::object); auto k=tag(j.at("type"),{"nativeBracket","nativeEstimate","zeroLength"});
    if(k=="zeroLength") { object(j,{"type","distance"}); require(num(j.at("distance"))==0,GUA_SPATIAL_SEMANTICS); return; }
    object(j,k=="nativeBracket"?std::initializer_list<const char*>{"type","source","error","safeFraction","unsafeFraction"}:std::initializer_list<const char*>{"type","source","error"},k=="nativeBracket"?std::initializer_list<const char*>{}:std::initializer_list<const char*>{"distance","fraction"});
    text(j,"source"); const auto& error=j.at("error");
    object(error,{"state"},{"absolute","reason"}); auto state=tag(error.at("state"),{"known","unknown"});
    if(state=="known") { require(has(error,"absolute")&&!has(error,"reason")); range(error.at("absolute"),0); }
    else { require(has(error,"reason")&&!has(error,"absolute")); text(error,"reason"); }
    if(k=="nativeBracket") { auto a=range(j.at("safeFraction"),0,1),b=range(j.at("unsafeFraction"),0,1); require(a<=b,GUA_SPATIAL_SEMANTICS); }
    else { require(has(j,"distance")||has(j,"fraction")); if(has(j,"distance")) range(j.at("distance"),0); if(has(j,"fraction")) range(j.at("fraction"),0,1); }
}
void result(const json& j) {
    object(j,{"schemaVersion","documentType","requestId","sessionEpoch","queryId","spaceId","spaceEpoch","kind","status"},
        {"error","outcome","coverage","truncated","sample","hits","nearest","originInside","initialOverlap","motion"});
    correlation(j); auto status=tag(j.at("status"),{"completed","rejected","unsupported","failed","cancelled","deadlineExceeded"});
    if(status!="completed") {
        require(j.fields.size()==10&&has(j,"error"),GUA_SPATIAL_SEMANTICS);
        const auto& e=j.at("error"); object(e,{"code"}); auto code=tag(e.at("code"),{"invalid_request","stale_space","not_authorized","unsupported_operation","unsupported_shape","unsupported_policy","unsupported_consistency","internal","cancelled","deadline_exceeded"});
        if(status=="rejected") require(code=="invalid_request"||code=="stale_space"||code=="not_authorized",GUA_SPATIAL_SEMANTICS);
        if(status=="unsupported") require(code.starts_with("unsupported_"),GUA_SPATIAL_SEMANTICS);
        if(status=="failed") require(code=="internal",GUA_SPATIAL_SEMANTICS);
        if(status=="cancelled") require(code=="cancelled",GUA_SPATIAL_SEMANTICS);
        if(status=="deadlineExceeded") require(code=="deadline_exceeded",GUA_SPATIAL_SEMANTICS);
        return;
    }
    require(!has(j,"error")&&has(j,"outcome")&&has(j,"coverage")&&has(j,"truncated")&&has(j,"sample")&&has(j,"hits"));
    coverage(j.at("coverage")); sample(j.at("sample")); const bool truncated=boolean(j.at("truncated"));
    const auto& hits=j.at("hits"); require(hits.type==json::array&&hits.items.size()<=32); for(auto& h:hits.items) hit(h);
    auto k=text(j,"kind"),o=text(j,"outcome");
    if(k=="raycast") {
        tag(j.at("outcome"),{"hit","noHit","indeterminate"}); require(has(j,"nearest")&&has(j,"originInside")&&!has(j,"initialOverlap")&&!has(j,"motion"));
        auto nearest=tag(j.at("nearest"),{"none","returnedHits","policyRegion"}); tag(j.at("originInside"),{"yes","no","unknown"});
        if(nearest=="policyRegion") require(text(j.at("coverage"),"state")=="complete",GUA_SPATIAL_SEMANTICS);
    } else {
        require(!has(j,"nearest")&&!has(j,"originInside"));
        if(k=="overlap") { tag(j.at("outcome"),{"detected","notDetected","indeterminate"}); require(!has(j,"initialOverlap")&&!has(j,"motion")); }
        else {
            tag(j.at("outcome"),{"initialOverlap","blocked","clear","indeterminate"}); require(has(j,"initialOverlap"));
            auto initial=tag(j.at("initialOverlap"),{"detected","notDetected","indeterminate"});
            if(initial=="detected") require(o=="initialOverlap",GUA_SPATIAL_SEMANTICS);
            if(initial=="indeterminate") require(o=="indeterminate",GUA_SPATIAL_SEMANTICS);
            if(o=="initialOverlap") require(initial=="detected",GUA_SPATIAL_SEMANTICS);
            if(o=="clear") require(initial=="notDetected"&&text(j.at("coverage"),"state")=="complete",GUA_SPATIAL_SEMANTICS);
            if(has(j,"motion")) motion(j.at("motion"));
        }
    }
    if(o=="noHit"||o=="notDetected"||o=="clear") require(hits.items.empty()&&!truncated,GUA_SPATIAL_SEMANTICS);
}
void string_set(const json& j,std::initializer_list<const char*> allowed={}) {
    require(j.type==json::array); std::set<std::string> seen;
    for(auto& item:j.items) { auto s=allowed.size()?tag(item,allowed):text(item); require(seen.insert(s).second); }
}
void provider(const json& j) {
    object(j,{"schemaVersion","documentType","providerId","spaceId","spaceEpoch","worldSpace","basis","up","unit","precision","operations","shapes","policies","consistencies","engine","limits"});
    text(j,"providerId"); text(j,"spaceId"); sequence(j.at("spaceEpoch"),true); require(text(j,"worldSpace")=="world3d");
    basis(j.at("basis")); unit(vector(j.at("up")));
    const auto& u=j.at("unit"); object(u,{"label"},{"metersPerUnit"}); text(u,"label"); if(has(u,"metersPerUnit")) positive(u.at("metersPerUnit"));
    const auto& p=j.at("precision"); object(p,{"representation","reason"},{"absoluteError"}); tag(p.at("representation"),{"binary32","binary64","unknown"}); text(p,"reason"); if(has(p,"absoluteError")) range(p.at("absoluteError"),0);
    string_set(j.at("operations"),{"raycast","overlap","sweep"}); string_set(j.at("shapes"),{"sphere","capsule","box"}); string_set(j.at("policies")); string_set(j.at("consistencies"),{"bestEffort","samePhysicsSample"});
    const auto& e=j.at("engine"); object(e,{"name","version","backend","backendVersion"}); for(auto k:{"name","version","backend","backendVersion"}) text(e,k);
    const auto& l=j.at("limits"); object(l,{"maxQueriesPerBatch","maxHitsPerQuery","maxDeadlineMs"});
    require(sequence(l.at("maxQueriesPerBatch"),true)<=64&&sequence(l.at("maxHitsPerQuery"),true)<=32); positive(l.at("maxDeadlineMs"));
}
std::string dump(const json& j) {
    if(j.type==json::string) return gua_value_detail::quote(j.text);
    if(j.type==json::number||j.type==json::boolean) return j.text;
    if(j.type==json::null) return "null";
    std::string s=j.type==json::array?"[":"{"; bool first=true;
    if(j.type==json::array) for(auto& v:j.items) { if(!first) s+=','; first=false; s+=dump(v); }
    else for(auto& [k,v]:j.fields) { if(!first) s+=','; first=false; s+=gua_value_detail::quote(k)+':'+dump(v); }
    return s+(j.type==json::array?"]":"}");
}
template<class F> int boundary(gua_spatial_error_t* error,F f) noexcept {
    if(error) *error={};
    try { f(); return 0; }
    catch(const gua_value_detail::failure& e) { if(error) { error->code=e.code; std::memcpy(error->path,e.path.data(),std::min(e.path.size(),size_t{127})); } return e.code; }
    catch(...) { if(error) { error->code=GUA_SPATIAL_INTERNAL; error->path[0]='$'; } return GUA_SPATIAL_INTERNAL; }
}
bool contains(const json& a,const std::string& v) { for(auto& x:a.items) if(x.text==v) return true; return false; }
void documents(const gua_spatial_document_t* a,int at,const gua_spatial_document_t* b,int bt) {
    require(a&&b&&a->type==at&&b->type==bt);
}
}
extern "C" {
int gua_spatial_from_json(const gua_spatial_parse_options_v1_t* options,gua_value_text_t input,gua_spatial_document_t** out,gua_spatial_error_t* error) {
    if(out) *out=nullptr;
    return boundary(error,[&] {
        require(out&&options&&options->struct_size==sizeof(*options)&&options->document_type>=1&&options->document_type<=3&&(!input.size||input.data));
        json j; try { j=gua_value_detail::parser(std::string_view(input.data?input.data:"",input.size)).parse(); }
        catch(const gua_value_detail::failure&) { fail(GUA_SPATIAL_INVALID); }
        require(j.type==json::object); require(text(j,"schemaVersion")=="spatial-r1",GUA_SPATIAL_VERSION,"$.schemaVersion");
        const char* types[]={"","request","result","provider"}; require(text(j,"documentType")==types[options->document_type]);
        if(options->document_type==1) request(j); else if(options->document_type==2) result(j); else provider(j);
        auto document=std::make_unique<gua_spatial_document_t>(); document->type=options->document_type; document->wire=dump(j); document->value=std::move(j); *out=document.release();
    });
}
int gua_spatial_document_type(const gua_spatial_document_t* d) { return d?d->type:0; }
int gua_spatial_copy_json(const gua_spatial_document_t* d,char* buffer,int capacity) {
    if(!d||capacity<0||(!buffer&&capacity)||d->wire.size()>=INT32_MAX) return 0;
    const int n=static_cast<int>(d->wire.size()+1);
    if(buffer&&capacity) { if(capacity>=n) std::memcpy(buffer,d->wire.c_str(),static_cast<size_t>(n)); else buffer[0]=0; }
    return n;
}
void gua_spatial_destroy(gua_spatial_document_t* d) { delete d; }
int gua_spatial_check_request(const gua_spatial_document_t* r,const gua_spatial_document_t* p,gua_spatial_error_t* error) {
    return boundary(error,[&] {
        documents(r,1,p,3); const auto& a=r->value; const auto& b=p->value;
        require(text(a,"spaceId")==text(b,"spaceId")&&sequence(a.at("spaceEpoch"))==sequence(b.at("spaceEpoch")),GUA_SPATIAL_CONTEXT);
        require(contains(b.at("operations"),text(a,"kind"))&&contains(b.at("policies"),text(a,"queryPolicyId"))&&contains(b.at("consistencies"),text(a,"consistency")),GUA_SPATIAL_UNSUPPORTED);
        if(has(a,"shape")) require(contains(b.at("shapes"),text(a.at("shape"),"type")),GUA_SPATIAL_UNSUPPORTED);
        const auto& l=b.at("limits"); require(num(a.at("deadlineMs"))<=num(l.at("maxDeadlineMs")),GUA_SPATIAL_INVALID);
        if(has(a,"maxHits")) require(sequence(a.at("maxHits"))<=sequence(l.at("maxHitsPerQuery")),GUA_SPATIAL_INVALID);
    });
}
int gua_spatial_check_result(const gua_spatial_document_t* r,const gua_spatial_document_t* result_document,gua_spatial_error_t* error) {
    return boundary(error,[&] {
        documents(r,1,result_document,2); const auto& a=r->value; const auto& b=result_document->value;
        for(auto k:{"queryId","spaceId","kind"}) require(text(a,k)==text(b,k),GUA_SPATIAL_CONTEXT);
        for(auto k:{"requestId","sessionEpoch","spaceEpoch"}) require(sequence(a.at(k))==sequence(b.at(k)),GUA_SPATIAL_CONTEXT);
        if(text(b,"status")!="completed") return;
        auto limit=has(a,"maxHits")?sequence(a.at("maxHits")):32;
        require(b.at("hits").items.size()<=static_cast<size_t>(limit),GUA_SPATIAL_SEMANTICS);
        if(has(b.at("sample"),"worldSnapshot")) require(sequence(b.at("sample").at("worldSnapshot").at("sessionEpoch"))==sequence(a.at("sessionEpoch")),GUA_SPATIAL_CONTEXT);
        if(text(a,"kind")!="sweep") return;
        const double distance=length(vector(a.at("delta")));
        if(distance==0) {
            require(text(b,"outcome")!="blocked"&&has(b,"motion")&&text(b.at("motion"),"type")=="zeroLength",GUA_SPATIAL_SEMANTICS);
        } else if(has(b,"motion")) {
            const auto& m=b.at("motion"); require(text(m,"type")!="zeroLength",GUA_SPATIAL_SEMANTICS);
            if(has(m,"distance")) require(num(m.at("distance"))<=distance,GUA_SPATIAL_SEMANTICS);
        }
    });
}
}
