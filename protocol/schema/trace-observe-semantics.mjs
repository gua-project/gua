/** Apply after trace.schema.json succeeds. Draft 2020-12 cannot compare values
 * at separate instance paths; these Observe association rules are normative. */
export function validateTraceObserveSemantics(record) {
  if (record.type !== "observation.change" || record.data.channel !== "observe") return true;
  const { received, catalogs } = record.data;
  if (Object.hasOwn(catalogs, "value")) return false;
  for (const side of ["before", "after"]) {
    const value = received[side], catalog = catalogs[side];
    const isEnum = value !== undefined && Object.hasOwn(value, "enumType");
    if (isEnum !== Object.hasOwn(catalogs, side)) return false;
    if (!isEnum) continue;
    const definition = catalog.enums[0];
    if (definition.enumType !== value.enumType) return false;
    const members = Array.isArray(value.value) ? value.value : [value.value];
    if (members.some(member => !definition.members.includes(member))) return false;
  }
  return true;
}
