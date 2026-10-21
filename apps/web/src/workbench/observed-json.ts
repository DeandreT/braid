import type { components } from "../api/generated";

type Segment = components["schemas"]["PathSegment"];
type JsonNode = {
  start: number;
  end: number;
  kind: "scalar" | "object" | "array";
  fields?: Map<string, JsonNode>;
  items?: JsonNode[];
};
type Refusal = "tooLarge" | "invalidJson" | "ambiguousJson" | "unsupportedPath";
export type ObservedValue =
  | {
      kind: "observed";
      values: readonly { text: string; truncated: boolean }[];
      more: boolean;
    }
  | { kind: "missing" }
  | { kind: "unavailable"; reason: Refusal };

class JsonRefusal extends Error {
  constructor(readonly reason: Refusal) {
    super("Sample value is unavailable.");
  }
}

/** Bounded lexical JSON index. Numbers are never converted to JavaScript Number. */
export class ObservedJson {
  static readonly maximumBytes = 256 * 1024;
  private readonly text: string;
  private readonly root: JsonNode;

  private constructor(bytes: Uint8Array) {
    if (bytes.length > ObservedJson.maximumBytes)
      throw new JsonRefusal("tooLarge");
    try {
      this.text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    } catch {
      throw new JsonRefusal("invalidJson");
    }
    this.root = new JsonIndex(this.text).parse();
  }

  static read(bytes: Uint8Array, path: readonly Segment[]): ObservedValue {
    try {
      return new ObservedJson(bytes).at(path);
    } catch (error) {
      return {
        kind: "unavailable",
        reason: error instanceof JsonRefusal ? error.reason : "invalidJson",
      };
    }
  }

  /** Concrete differing locations only; invalid or ambiguous JSON yields no guessed locations. */
  static changedPaths(
    before: Uint8Array,
    after: Uint8Array,
  ): readonly (readonly Segment[])[] | null {
    try {
      const left = new ObservedJson(before),
        right = new ObservedJson(after);
      const paths: Segment[][] = [];
      const visit = (
        a: JsonNode | undefined,
        b: JsonNode | undefined,
        path: Segment[],
      ) => {
        if (paths.length >= 64) return;
        if (!a || !b || a.kind !== b.kind) {
          paths.push(path);
          return;
        }
        if (a.kind === "scalar") {
          if (
            left.text.slice(a.start, a.end) !== right.text.slice(b.start, b.end)
          )
            paths.push(path);
        } else if (a.kind === "object") {
          for (const key of new Set([...a.fields!.keys(), ...b.fields!.keys()]))
            visit(a.fields!.get(key), b.fields!.get(key), [
              ...path,
              { kind: "field", name: key },
            ]);
        } else {
          for (
            let index = 0;
            index < Math.max(a.items!.length, b.items!.length);
            index++
          )
            visit(a.items![index], b.items![index], [
              ...path,
              { kind: "index", index: String(index) },
            ]);
        }
      };
      visit(left.root, right.root, []);
      return paths;
    } catch {
      return null;
    }
  }

  private at(path: readonly Segment[]): ObservedValue {
    if (path.length > 64) throw new JsonRefusal("unsupportedPath");
    let nodes = [this.root];
    for (const segment of path) {
      const next: JsonNode[] = [];
      for (const node of nodes) {
        if (segment.kind === "field") {
          if (
            segment.namespaceUri !== null &&
            segment.namespaceUri !== undefined
          )
            throw new JsonRefusal("unsupportedPath");
          const value = node.fields?.get(segment.name);
          if (value) next.push(value);
        } else if (segment.kind === "index") {
          if (!/^(0|[1-9][0-9]*)$/.test(segment.index))
            throw new JsonRefusal("unsupportedPath");
          const index = BigInt(segment.index);
          if (node.items && index < BigInt(node.items.length))
            next.push(node.items[Number(index)]!);
        } else if (segment.kind === "anyItem") next.push(...(node.items ?? []));
        else throw new JsonRefusal("unsupportedPath");
      }
      nodes = next;
    }
    if (!nodes.length) return { kind: "missing" };
    return {
      kind: "observed",
      more: nodes.length > 16,
      values: nodes.slice(0, 16).map((node) => ({
        text: this.text.slice(node.start, Math.min(node.end, node.start + 256)),
        truncated: node.end - node.start > 256,
      })),
    };
  }
}

class JsonIndex {
  private offset = 0;
  private count = 0;
  constructor(private readonly text: string) {}
  parse(): JsonNode {
    const value = this.value(0);
    this.space();
    if (this.offset !== this.text.length) this.invalid();
    return value;
  }
  private space(): void {
    while (/[\t\n\r ]/.test(this.text[this.offset] ?? "x")) this.offset++;
  }
  private value(depth: number): JsonNode {
    if (depth > 64 || ++this.count > 20000) throw new JsonRefusal("tooLarge");
    this.space();
    const start = this.offset,
      first = this.text[this.offset];
    if (first === "{") {
      this.offset++;
      this.space();
      const fields = new Map<string, JsonNode>();
      if (this.text[this.offset] !== "}")
        while (true) {
          this.space();
          const key = this.string();
          if (fields.has(key)) throw new JsonRefusal("ambiguousJson");
          this.space();
          if (this.text[this.offset++] !== ":") this.invalid();
          fields.set(key, this.value(depth + 1));
          this.space();
          if (this.text[this.offset] === "}") break;
          if (this.text[this.offset++] !== ",") this.invalid();
        }
      this.offset++;
      return { start, end: this.offset, kind: "object", fields };
    }
    if (first === "[") {
      this.offset++;
      this.space();
      const items: JsonNode[] = [];
      if (this.text[this.offset] !== "]")
        while (true) {
          items.push(this.value(depth + 1));
          this.space();
          if (this.text[this.offset] === "]") break;
          if (this.text[this.offset++] !== ",") this.invalid();
        }
      this.offset++;
      return { start, end: this.offset, kind: "array", items };
    }
    if (first === '"') this.string();
    else {
      const match =
        /^(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)/.exec(
          this.text.slice(this.offset),
        );
      if (!match) this.invalid();
      this.offset += match[0].length;
    }
    const last = this.text[this.offset];
    if (last !== undefined && !/[\t\n\r ,}\]]/.test(last)) this.invalid();
    return { start, end: this.offset, kind: "scalar" };
  }
  private string(): string {
    const start = this.offset;
    if (this.text[this.offset++] !== '"') this.invalid();
    while (this.offset < this.text.length) {
      const char = this.text[this.offset++];
      if (char === '"') {
        try {
          return JSON.parse(this.text.slice(start, this.offset)) as string;
        } catch {
          this.invalid();
        }
      }
      if (char === "\\") this.offset++;
    }
    return this.invalid();
  }
  private invalid(): never {
    throw new JsonRefusal("invalidJson");
  }
}
