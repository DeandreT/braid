import { readFileSync, statSync } from "node:fs";
import { setTimeout as pause } from "node:timers/promises";

function processStart(pid) {
  const text = readFileSync(`/proc/${pid}/stat`, "utf8");
  const end = text.lastIndexOf(") ");
  const fields = text.slice(end + 2).split(" ");
  if (
    end < 0 ||
    !text.startsWith(`${pid} (`) ||
    !/^[0-9]+$/.test(fields[1] ?? "") ||
    !/^[0-9]+$/.test(fields[19] ?? "") ||
    ["X", "Z"].includes(fields[0])
  ) {
    throw new Error("process cannot be attributed to the original child");
  }
  return { parent: Number(fields[1]), startTicks: fields[19] };
}

function executable(path) {
  const { dev, ino } = statSync(path, { bigint: true });
  return `${dev}:${ino}`;
}

function snapshot(pid) {
  const first = processStart(pid);
  const before = executable(`/proc/${pid}/exe`);
  const argv = readFileSync(`/proc/${pid}/cmdline`);
  const after = executable(`/proc/${pid}/exe`);
  const last = processStart(pid);
  if (last.parent !== first.parent || last.startTicks !== first.startTicks) {
    throw new Error("process changed during launch observation");
  }
  return { ...last, before, after, argv };
}

function classify(sample, launcher, target) {
  if (
    sample.before === target.image &&
    sample.after === target.image &&
    sample.argv.equals(target.argv)
  ) {
    return "executed";
  }
  // Bun can publish the spawn callback while the original Linux child
  // still holds the launcher's image. Permit only that exact predecessor
  // or a sample that straddles its transition to the admitted executable.
  if (
    (sample.before === launcher.image &&
      ((sample.after === launcher.image && sample.argv.equals(launcher.argv)) ||
        (sample.after === target.image &&
          (sample.argv.equals(launcher.argv) ||
            sample.argv.equals(target.argv))))) ||
    // Linux can publish the new executable before populating the new
    // argument table. Empty argv is not a successful admission.
    (sample.after === target.image &&
      (sample.before === launcher.image || sample.before === target.image) &&
      sample.argv.length === 0)
  ) {
    return "executing";
  }
  return "foreign";
}

export function hostExecRequest(pid, command) {
  if (!Number.isSafeInteger(pid) || pid <= 1) throw new Error("missing child");
  const { parent, startTicks } = processStart(pid);
  if (parent !== process.pid) throw new Error("foreign child");
  return {
    pid,
    command,
    parent,
    startTicks,
    launcher: {
      image: executable("/proc/self/exe"),
      argv: readFileSync("/proc/self/cmdline"),
    },
    target: {
      image: executable(command[0]),
      argv: Buffer.from(`${command.join("\0")}\0`),
    },
  };
}

export async function observeHostExec(
  request,
  alive,
  end,
  observe = snapshot,
  wait = pause,
  now = Date.now,
) {
  const { pid, command, parent, startTicks, launcher, target } = request;
  const observation = { sawOriginal: false, argvArityTwo: false };
  const refuse = () => ({ identity: null, ...observation });
  // This is a bounded observation of the one already spawned child, not
  // another launch. The existing complete host-start deadline still applies.
  try {
    while (alive() && now() < end) {
      const sample = observe(pid);
      if (sample.parent !== parent || sample.startTicks !== startTicks) {
        return refuse();
      }
      observation.sawOriginal = true;
      const firstTerminator = sample.argv.indexOf(0);
      observation.argvArityTwo =
        firstTerminator >= 0 &&
        firstTerminator < sample.argv.length - 1 &&
        sample.argv.indexOf(0, firstTerminator + 1) === sample.argv.length - 1;
      const state = classify(sample, launcher, target);
      if (state === "executed") {
        return alive() && now() < end
          ? { identity: { pid, startTicks, command }, ...observation }
          : refuse();
      }
      if (state !== "executing") return refuse();
      await wait(Math.min(5, Math.max(1, end - now())));
    }
  } catch {
    // An unreadable, exited or changed process is not a launch receipt.
  }
  return refuse();
}
