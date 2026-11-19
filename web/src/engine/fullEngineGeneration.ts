export function createFullEngineGeneration() {
  let generation = 0;
  return {
    begin() { return ++generation; },
    isCurrent(candidate = 0) { return candidate === generation; },
    invalidate() { generation++; },
  };
}
