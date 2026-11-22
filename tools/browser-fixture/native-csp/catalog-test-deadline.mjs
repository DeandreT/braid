import { setTimeout as delay } from "node:timers/promises";
export async function bounded(promise, why) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise(
        (_, reject) =>
          (timer = setTimeout(() => reject(Error("deadline: " + why)), 5000)),
      ),
    ]);
  } finally {
    clearTimeout(timer);
  }
}
export function until(read, matched, why) {
  const end = performance.now() + 5000;
  return bounded(
    (async () => {
      while (performance.now() < end) {
        const observation = await read();
        if (matched(observation)) return observation;
        await delay(10);
      }
      throw Error("deadline: " + why);
    })(),
    why,
  );
}
