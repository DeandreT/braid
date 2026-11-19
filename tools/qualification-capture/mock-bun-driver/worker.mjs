// Fixed four-byte mock request and reply. No file handles, FFI, or terminal effects.
self.onmessage = ({ data }) => {
  if (data.hold) return;
  if (!(data.bytes instanceof Uint8Array) || data.bytes.length !== 4)
    throw Error("invalidRequest");
  const view = new DataView(data.bytes.buffer, data.bytes.byteOffset, 4);
  view.setUint32(0, view.getUint32(0, true) + 1, true);
  self.postMessage(data.bytes);
};
