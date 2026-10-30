/** Adds incoming rows to a sequence-ordered list, ignoring ids already present (a live event and a REST sync can overlap). */
export function mergeBySequence<T extends { id: string; sequence: number | string }>(existing: readonly T[], incoming: readonly T[]): T[] {
  const known = new Set(existing.map((x) => x.id));
  const fresh = incoming.filter((x) => !known.has(x.id));
  return fresh.length ? [...existing, ...fresh].sort((a, b) => Number(a.sequence) - Number(b.sequence)) : [...existing];
}
