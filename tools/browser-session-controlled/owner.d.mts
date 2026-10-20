export class BrowserSessionStaticOwner {
  static open(configurationPath: string): Promise<BrowserSessionStaticOwner>;
  readonly origin: string;
  readonly unexpectedApi: number;
  readonly settled: boolean;
  readonly closeFacts: Readonly<{
    unexpectedApi: number;
    pendingRequests: number;
    sockets: number;
    serverCloseStarted: boolean;
    serverCloseSettled: boolean;
    settled: boolean;
  }>;
  close(): Promise<void>;
  toString(): string;
}
