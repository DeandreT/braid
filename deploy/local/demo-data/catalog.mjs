// The demo catalog: self-authored projects, mappings, revisions, samples and regression cases.
// Every company, person, SKU and document here is invented. `native(revision)` returns the
// native document of revision 1..`revisions`; later revisions change the output, so version
// history, comparison and automatic regression checks have something to show.
//
// Samples feed previews and regression cases. A case pinned to revision 1 keeps revision 1's
// output as its baseline, so the automatic check of a later revision that changed the output
// fails, as it would for a real change. `stale` cases carry an outdated hand-written baseline
// and fail when run. `compile` marks mappings the seed also approves and builds.
import {
  call,
  concat,
  constant,
  count,
  field,
  fmt,
  group,
  list,
  mapping,
  now,
  parameter,
  position,
  scalar,
  scope,
  segment,
  top,
  upper,
  when,
} from "./native.mjs";

const json = (value) => `${JSON.stringify(value, null, 2)}\n`;
const number = (expression) => call("to_number", expression);
const eq = (expression, value) => call("equal", expression, constant(value));
const gt = (expression, value) =>
  call("greater_than", expression, constant(value));
const since = (revision, from, value) => (revision >= from ? value : undefined);
const fields = (...entries) => entries.filter(Boolean);
const bindings = (entries) =>
  Object.fromEntries(Object.entries(entries).filter(([, value]) => value));

// ---- EDI building blocks.
const x12Options = { edi_kind: "x12" };
const edifactOptions = { edi_kind: "edifact" };
const x12Envelope = [segment("ISA", 16), segment("GS", 8), segment("ST", 2)];
const x12Trailer = [segment("SE", 2), segment("GE", 2), segment("IEA", 2)];
const components = (name, parts) =>
  group(
    name,
    Array.from({ length: parts }, (_, index) => scalar(`c${index + 1}`)),
  );
// elements: the component count of each element (1 for a simple element).
const edifactSegment = (tag, elements, repeating = false) =>
  group(
    tag,
    elements.map((parts, index) =>
      parts > 1 ? components(`e${index + 1}`, parts) : scalar(`e${index + 1}`),
    ),
    repeating,
  );

// A complete single-transaction X12 interchange around `body` (first line is the ST01 code).
export function x12Document(sender, receiver, functional, control, body) {
  const lines = body.trim().split("\n");
  const padded = String(control).padStart(9, "0");
  return [
    `ISA*00*          *00*          *ZZ*${sender.padEnd(15)}*ZZ*${receiver.padEnd(15)}*261102*0930*^*00501*${padded}*0*T*:~`,
    `GS*${functional}*${sender}*${receiver}*20261102*0930*${control}*X*005010~`,
    `ST*${lines[0]}*0001~`,
    ...lines.slice(1),
    `SE*${lines.length + 1}*0001~`,
    `GE*1*${control}~`,
    `IEA*1*${padded}~`,
    "",
  ].join("\n");
}

// ---------------------------------------------------------------- Order fulfilment
const order = (orderId, placedAt, channel, customer, lines) => ({
  orderId,
  placedAt,
  channel,
  customer,
  lines,
});
const orderLine = (sku, description, qty, bin, weightKg) => ({
  sku,
  description,
  qty,
  bin,
  weightKg,
});
const goldOrder = order(
  "WEB-104233",
  "2026-11-02T08:14:00Z",
  "web",
  { name: "Priya Raman", email: "priya.raman@example.com", tier: "gold" },
  [
    orderLine("MUG-12OZ-BLU", "Stoneware mug, blue", 4, "C-12-03", 0.42),
    orderLine("KETTLE-1L-STL", "Steel kettle 1 L", 1, "A-02-11", 1.1),
    orderLine("TEA-ASSAM-500G", "Assam tea 500 g", 0, "B-07-01", 0.5),
    orderLine("TRIVET-CORK", "Cork trivet", 2, "C-01-09", 0.12),
  ],
);
const marketplaceOrder = order(
  "MKT-55102",
  "2026-11-02T09:02:00Z",
  "marketplace",
  { name: "Tomasz Nowak", email: "t.nowak@example.net", tier: "silver" },
  [
    orderLine("PLATE-26CM-WHT", "Dinner plate 26 cm", 6, "D-04-02", 0.61),
    orderLine("BOWL-15CM-WHT", "Cereal bowl 15 cm", 6, "D-04-05", 0.38),
  ],
);
const orderSchema = group("Order", [
  scalar("orderId"),
  scalar("placedAt"),
  scalar("channel"),
  group("customer", [scalar("name"), scalar("email"), scalar("tier")]),
  list("lines", [
    scalar("sku"),
    scalar("description"),
    scalar("qty", "int"),
    scalar("bin"),
    scalar("weightKg", "float"),
  ]),
]);

const pickList = {
  slug: "pick-list",
  name: "Web orders to pick list",
  description:
    "Turns a storefront order into a warehouse pick list: one pick per line, walked in bin order.",
  revisions: 3,
  native: (revision) =>
    mapping({
      source: orderSchema,
      target: group(
        "PickList",
        fields(
          scalar("pickListId"),
          scalar("orderId"),
          since(revision, 2, scalar("priority")),
          scalar("customer"),
          since(revision, 3, scalar("lineCount", "int")),
          list(
            "picks",
            fields(
              scalar("seq", "int"),
              scalar("bin"),
              scalar("sku"),
              scalar("qty", "int"),
              since(revision, 3, scalar("label")),
            ),
          ),
        ),
      ),
      root: {
        set: bindings({
          pickListId: concat("PL-", field("orderId")),
          orderId: field("orderId"),
          priority: since(
            revision,
            2,
            when(
              eq(field("customer", "tier"), "gold"),
              constant("rush"),
              constant("standard"),
            ),
          ),
          customer: field("customer", "name"),
          lineCount: since(revision, 3, count("lines")),
        }),
        children: [
          scope("picks", {
            from: ["lines"],
            sortBy: since(revision, 2, field("bin")),
            filter: since(revision, 3, gt(field("qty"), 0)),
            set: bindings({
              seq: position("lines"),
              bin: field("bin"),
              sku: field("sku"),
              qty: field("qty"),
              label: since(
                revision,
                3,
                concat(field("sku"), " x", field("qty")),
              ),
            }),
          }),
        ],
      },
    }),
  samples: [
    { name: "Gold customer, four lines", input: json(goldOrder) },
    { name: "Marketplace order", input: json(marketplaceOrder) },
  ],
  cases: [
    { name: "Gold order (first baseline)", sample: 0, revision: 1 },
    { name: "Marketplace order", sample: 1, revision: "head" },
    { name: "Gold order with labels", sample: 0, revision: "head" },
  ],
  compile: true,
};

const purchaseOrder850 = x12Document(
  "NORTHWINDRET",
  "WEFTWMS",
  "PO",
  4101,
  `850
BEG*00*SA*PO-77812**20261102~
REF*DP*042~
N1*ST*Northwind Store 118*92*0118~
PO1*1*24*EA*4.75**VN*MUG-12OZ-BLU~
PO1*2*12*EA*18.00**VN*KETTLE-1L-STL~
PO1*3*6*CS*42.50**VN*TEA-ASSAM-500G~
CTT*3~`,
);
const purchaseOrder850Small = x12Document(
  "NORTHWINDRET",
  "WEFTWMS",
  "PO",
  4102,
  `850
BEG*00*SA*PO-77840**20261103~
REF*DP*017~
N1*ST*Northwind Store 204*92*0204~
PO1*1*48*EA*1.95**VN*NAPKIN-LINEN-4PK~
CTT*1~`,
);
const purchaseOrderFromX12 = {
  slug: "x12-850-order",
  name: "X12 850 purchase order to order JSON",
  description:
    "Reads a retailer's 850 purchase order (one transaction per interchange) into the order JSON the warehouse API accepts.",
  revisions: 3,
  native: (revision) =>
    mapping({
      source: group("Interchange", [
        ...x12Envelope,
        segment("BEG", 5),
        segment("REF", 2),
        segment("N1", 4),
        segment("PO1", 7, true),
        segment("CTT", 1),
        ...x12Trailer,
      ]),
      target: group(
        "Order",
        fields(
          scalar("poNumber"),
          scalar("orderDate"),
          scalar("department"),
          scalar("sender"),
          group("shipTo", [scalar("name"), scalar("storeNumber")]),
          since(
            revision,
            2,
            list(
              "lines",
              fields(
                scalar("line"),
                scalar("sku"),
                scalar("quantity", "float"),
                scalar("unit"),
                scalar("unitPrice", "float"),
                since(revision, 3, scalar("extended", "float")),
              ),
            ),
          ),
          since(revision, 3, scalar("lineCount", "int")),
        ),
      ),
      sourceOptions: x12Options,
      root: {
        set: bindings({
          poNumber: field("BEG", "e3"),
          orderDate: field("BEG", "e5"),
          department: field("REF", "e2"),
          sender: call("trim", field("ISA", "e6")),
          lineCount: since(revision, 3, count("PO1")),
        }),
        children: fields(
          scope("shipTo", {
            set: { name: field("N1", "e2"), storeNumber: field("N1", "e4") },
          }),
          since(
            revision,
            2,
            scope("lines", {
              from: ["PO1"],
              set: bindings({
                line: field("e1"),
                sku: field("e7"),
                quantity: number(field("e2")),
                unit: field("e3"),
                unitPrice: number(field("e4")),
                extended: since(
                  revision,
                  3,
                  call("multiply", number(field("e2")), number(field("e4"))),
                ),
              }),
            }),
          ),
        ),
      },
    }),
  samples: [
    { name: "Three-line store order", input: purchaseOrder850, document: true },
    {
      name: "Single-line replenishment",
      input: purchaseOrder850Small,
      document: true,
    },
  ],
  cases: [
    { name: "Three-line order (header only baseline)", sample: 0, revision: 1 },
    { name: "Three-line order", sample: 0, revision: "head" },
    { name: "Replenishment order", sample: 1, revision: "head" },
  ],
  compile: true,
};

const routing = {
  slug: "order-routing",
  name: "Order priority routing",
  description:
    "Chooses the fulfilment route for an order from run-time parameters: the shipping warehouse, the same-day cut-off and an express-only switch.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: orderSchema,
      target: group(
        "Route",
        fields(
          scalar("orderId"),
          scalar("warehouse"),
          scalar("route"),
          since(revision, 2, scalar("note")),
          scalar("decidedAt"),
        ),
      ),
      root: {
        set: bindings({
          orderId: field("orderId"),
          warehouse: parameter("warehouse"),
          route: when(
            parameter("expressOnly", "bool"),
            constant("express"),
            when(
              eq(field("customer", "tier"), "gold"),
              constant("priority"),
              constant("ground"),
            ),
          ),
          note: since(
            revision,
            2,
            concat(
              "Cut-off ",
              call("string", parameter("cutoffHour", "int")),
              ":00 local",
            ),
          ),
          decidedAt: now(),
        }),
      },
    }),
  samples: [
    {
      name: "Gold order from Reno",
      input: json(goldOrder),
      parameters: [
        { name: "warehouse", kind: "string", value: "RNO-1" },
        { name: "expressOnly", kind: "boolean", value: "false" },
        { name: "cutoffHour", kind: "integer", value: "14" },
      ],
    },
    {
      name: "Express override",
      input: json(marketplaceOrder),
      parameters: [
        { name: "warehouse", kind: "string", value: "ATL-2" },
        { name: "expressOnly", kind: "boolean", value: "true" },
        { name: "cutoffHour", kind: "integer", value: "16" },
      ],
    },
  ],
  cases: [
    {
      name: "Express override (outdated baseline)",
      sample: 1,
      revision: "head",
      stale: true,
    },
  ],
};

const stock = (sku, ordered, available, eta) => ({
  sku,
  ordered,
  available,
  eta,
});
const backorders = {
  slug: "backorders",
  name: "Backorder report",
  description:
    "Lists the order lines the warehouse cannot fill today, with the shortfall and the expected restock date.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: group("Availability", [
        scalar("orderId"),
        list("lines", [
          scalar("sku"),
          scalar("ordered", "int"),
          scalar("available", "int"),
          scalar("eta"),
        ]),
      ]),
      target: group("Backorders", [
        scalar("orderId"),
        list("items", [
          scalar("sku"),
          scalar("shortfall", "int"),
          scalar("restock"),
        ]),
      ]),
      root: {
        set: { orderId: field("orderId") },
        children: [
          scope("items", {
            from: ["lines"],
            filter: call("less_than", field("available"), field("ordered")),
            sortBy: since(
              revision,
              2,
              call("subtract", field("available"), field("ordered")),
            ),
            set: {
              sku: field("sku"),
              shortfall: call("subtract", field("ordered"), field("available")),
              restock:
                revision >= 2
                  ? call(
                      "substitute_missing",
                      field("eta"),
                      constant("unknown"),
                    )
                  : field("eta"),
            },
          }),
        ],
      },
    }),
  samples: [
    {
      name: "Two short lines",
      input: json({
        orderId: "WEB-104233",
        lines: [
          stock("MUG-12OZ-BLU", 4, 4, "2026-11-04"),
          stock("KETTLE-1L-STL", 3, 1, "2026-11-09"),
          stock("TEA-ASSAM-500G", 10, 0),
        ],
      }),
    },
  ],
  cases: [{ name: "Two short lines", sample: 0, revision: "head" }],
  compile: true,
};

const acknowledgement = {
  slug: "x12-855-ack",
  name: "Order acknowledgement to X12 855",
  description:
    "Writes the warehouse's order acceptance as an X12 855 purchase order acknowledgement for the retailer.",
  revisions: 2,
  native: (revision) => {
    const fixed = (values) =>
      Object.fromEntries(
        values.map((value, index) => [`e${index + 1}`, constant(value)]),
      );
    return mapping({
      source: group("Acknowledgement", [
        scalar("poNumber"),
        scalar("ackDate"),
        scalar("control"),
        list("lines", [
          scalar("line"),
          scalar("qty"),
          scalar("sku"),
          scalar("status"),
        ]),
      ]),
      target: group("Interchange", [
        ...x12Envelope,
        segment("BAK", 4),
        segment("PO1", 7, true),
        segment("ACK", 3, true),
        ...x12Trailer,
      ]),
      targetOptions: x12Options,
      root: {
        children: [
          scope("ISA", {
            set: {
              ...fixed([
                "00",
                "          ",
                "00",
                "          ",
                "ZZ",
                "WEFTWMS        ",
                "ZZ",
                "NORTHWINDRET   ",
              ]),
              e9: constant("261102"),
              e10: constant("1015"),
              e11: constant("^"),
              e12: constant("00501"),
              e13: call(
                "pad_string_left",
                field("control"),
                constant(9),
                constant("0"),
              ),
              e14: constant("0"),
              e15: constant("T"),
              e16: constant(":"),
            },
          }),
          scope("GS", {
            set: {
              ...fixed(["PR", "WEFTWMS", "NORTHWINDRET", "20261102", "1015"]),
              e6: field("control"),
              e7: constant("X"),
              e8: constant("005010"),
            },
          }),
          scope("ST", { set: { e1: constant("855"), e2: constant("0001") } }),
          scope("BAK", {
            set: {
              e1: constant("00"),
              e2: constant(revision >= 2 ? "AC" : "AD"),
              e3: field("poNumber"),
              e4: field("ackDate"),
            },
          }),
          scope("PO1", {
            from: ["lines"],
            set: {
              e1: field("line"),
              e2: field("qty"),
              e3: constant("EA"),
              e6: constant("VN"),
              e7: field("sku"),
            },
          }),
          scope("ACK", {
            from: ["lines"],
            set: {
              e1:
                revision >= 2
                  ? when(
                      eq(field("status"), "short"),
                      constant("IQ"),
                      constant("IA"),
                    )
                  : constant("IA"),
              e2: field("qty"),
              e3: constant("EA"),
            },
          }),
          scope("SE", {
            set: {
              e1: call(
                "string",
                call(
                  "add",
                  constant(3),
                  call("multiply", count("lines"), constant(2)),
                ),
              ),
              e2: constant("0001"),
            },
          }),
          scope("GE", { set: { e1: constant("1"), e2: field("control") } }),
          scope("IEA", {
            set: {
              e1: constant("1"),
              e2: call(
                "pad_string_left",
                field("control"),
                constant(9),
                constant("0"),
              ),
            },
          }),
        ],
      },
    });
  },
  samples: [
    {
      name: "Accepted with one short line",
      input: json({
        poNumber: "PO-77812",
        ackDate: "20261102",
        control: "5201",
        lines: [
          { line: "1", qty: "24", sku: "MUG-12OZ-BLU", status: "ok" },
          { line: "2", qty: "12", sku: "KETTLE-1L-STL", status: "short" },
        ],
      }),
      document: true,
    },
  ],
  cases: [
    {
      name: "Accepted with one short line",
      sample: 0,
      revision: "head",
      edi: "x12",
    },
  ],
};

// ---------------------------------------------------------------- Invoicing
const invoiceSource = group("Shipment", [
  scalar("shipmentId"),
  scalar("shippedOn"),
  group("billTo", [scalar("account"), scalar("name"), scalar("country")]),
  list("lines", [
    scalar("sku"),
    scalar("qty", "int"),
    scalar("unitPrice", "float"),
    scalar("discountPct", "float"),
  ]),
]);
const shipmentToBill = {
  shipmentId: "SHP-88120",
  shippedOn: "2026-11-03",
  billTo: { account: "ACC-0042", name: "Harbor & Pine Cafe", country: "US" },
  lines: [
    { sku: "BEANS-HOUSE-1KG", qty: 12, unitPrice: 16.4, discountPct: 0 },
    { sku: "CUP-8OZ-COMP", qty: 500, unitPrice: 0.11, discountPct: 5 },
    { sku: "LID-8OZ-COMP", qty: 500, unitPrice: 0.04, discountPct: 5 },
  ],
};
const shipmentToBillEu = {
  shipmentId: "SHP-88131",
  shippedOn: "2026-11-04",
  billTo: { account: "ACC-0107", name: "Kaffeehaus Lindner", country: "DE" },
  lines: [{ sku: "BEANS-DECAF-1KG", qty: 4, unitPrice: 18.9, discountPct: 10 }],
};
const lineAmount = call("multiply", field("qty"), field("unitPrice"));
const discounted = call(
  "multiply",
  lineAmount,
  call(
    "subtract",
    constant(1),
    call("divide", field("discountPct"), constant(100)),
  ),
);

const invoiceFromShipment = {
  slug: "invoice-from-shipment",
  name: "Invoice from shipment",
  description:
    "Prices a confirmed shipment into a customer invoice with per-line amounts and display text.",
  revisions: 3,
  native: (revision) =>
    mapping({
      source: invoiceSource,
      target: group(
        "Invoice",
        fields(
          scalar("invoiceNumber"),
          scalar("account"),
          scalar("customer"),
          since(revision, 2, scalar("currency")),
          list(
            "lines",
            fields(
              scalar("sku"),
              scalar("qty", "int"),
              scalar("amount", "float"),
              since(revision, 3, scalar("display")),
            ),
          ),
        ),
      ),
      root: {
        set: bindings({
          invoiceNumber: concat(
            "INV-",
            call("substring_after", field("shipmentId"), constant("SHP-")),
          ),
          account: field("billTo", "account"),
          customer: upper(field("billTo", "name")),
          currency: since(revision, 2, parameter("currency")),
        }),
        children: [
          scope("lines", {
            from: ["lines"],
            set: bindings({
              sku: field("sku"),
              qty: field("qty"),
              amount:
                revision >= 3
                  ? call("round", discounted, constant(2))
                  : call("round", lineAmount, constant(2)),
              display: since(
                revision,
                3,
                concat(field("sku"), " / ", fmt(discounted)),
              ),
            }),
          }),
        ],
      },
    }),
  samples: [
    {
      name: "Cafe supplies (USD)",
      input: json(shipmentToBill),
      parameters: [{ name: "currency", kind: "string", value: "USD" }],
    },
    {
      name: "German account (EUR)",
      input: json(shipmentToBillEu),
      parameters: [{ name: "currency", kind: "string", value: "EUR" }],
    },
  ],
  cases: [
    { name: "Cafe supplies (USD)", sample: 0, revision: "head" },
    { name: "German account (EUR)", sample: 1, revision: "head" },
    { name: "Cafe supplies (before discounts)", sample: 0, revision: 2 },
    {
      name: "Cafe supplies (outdated totals)",
      sample: 0,
      revision: "head",
      stale: true,
    },
  ],
  compile: true,
};

const ledger = {
  slug: "invoice-ledger",
  name: "Invoice ledger postings",
  description:
    "Posts invoice lines to the general ledger. The chart of accounts arrives as a second named input, so the revenue and tax accounts can change without a new revision.",
  revisions: 2,
  native: (revision) => ({
    ...mapping({
      source: group("Invoice", [
        scalar("invoiceNumber"),
        scalar("postingDate"),
        list("lines", [
          scalar("sku"),
          scalar("amount", "float"),
          scalar("tax", "float"),
        ]),
      ]),
      target: group(
        "Journal",
        fields(
          scalar("reference"),
          scalar("postingDate"),
          list(
            "entries",
            fields(
              scalar("account"),
              scalar("credit", "float"),
              since(revision, 2, scalar("memo")),
            ),
          ),
          since(revision, 2, scalar("entryCount", "int")),
        ),
      ),
      root: {
        set: bindings({
          reference: field("invoiceNumber"),
          postingDate: field("postingDate"),
          entryCount: since(revision, 2, count("lines")),
        }),
        children: [
          scope("entries", {
            from: ["lines"],
            set: bindings({
              account: top("accounts", "revenue"),
              credit: field("amount"),
              memo: since(
                revision,
                2,
                concat(
                  field("sku"),
                  " (tax ",
                  fmt(field("tax")),
                  " to ",
                  top("accounts", "tax"),
                  ")",
                ),
              ),
            }),
          }),
        ],
      },
    }),
    extra_sources: [
      {
        name: "accounts",
        path: "accounts.json",
        schema: group("Accounts", [scalar("revenue"), scalar("tax")]),
      },
    ],
  }),
  samples: [
    {
      name: "Invoice INV-88120",
      input: json({
        invoiceNumber: "INV-88120",
        postingDate: "2026-11-03",
        lines: [
          { sku: "BEANS-HOUSE-1KG", amount: 196.8, tax: 15.74 },
          { sku: "CUP-8OZ-COMP", amount: 52.25, tax: 4.18 },
        ],
      }),
      named: [
        {
          name: "accounts",
          content: json({ revenue: "4000-SALES", tax: "2200-TAX-PAYABLE" }),
        },
      ],
    },
  ],
  cases: [{ name: "Invoice INV-88120", sample: 0, revision: "head" }],
  compile: true,
};

const invoiceCsv = {
  slug: "invoice-csv",
  name: "Invoice CSV export",
  description:
    "Flattens invoices into the CSV the finance team imports into its accounting package.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: invoiceSource,
      target: group("Rows", [
        list(
          "Row",
          fields(
            scalar("shipment"),
            scalar("account"),
            scalar("sku"),
            scalar("qty", "int"),
            scalar("amount"),
            since(revision, 2, scalar("country")),
          ),
        ),
      ]),
      targetPath: "invoices.csv",
      targetOptions: {
        delimiter: revision >= 2 ? ";" : ",",
        has_header_row: true,
      },
      root: {
        children: [
          scope("Row", {
            from: ["lines"],
            set: bindings({
              shipment: top("shipmentId"),
              account: top("billTo", "account"),
              sku: field("sku"),
              qty: field("qty"),
              amount: fmt(lineAmount),
              country: since(revision, 2, top("billTo", "country")),
            }),
          }),
        ],
      },
    }),
  samples: [],
  cases: [],
};

const invoice810 = x12Document(
  "WEFTWMS",
  "HARBORPINE",
  "IN",
  6120,
  `810
BIG*20261103*INV-88120**PO-5531~
N1*BT*Harbor & Pine Cafe*91*ACC-0042~
IT1*1*12*EA*16.40**VN*BEANS-HOUSE-1KG~
IT1*2*500*EA*0.11**VN*CUP-8OZ-COMP~
IT1*3*500*EA*0.04**VN*LID-8OZ-COMP~
TDS*27180~`,
);
const invoiceSummary = {
  slug: "x12-810-summary",
  name: "X12 810 invoice summary",
  description:
    "Summarises an outbound 810 invoice for the customer portal: header, bill-to and line totals.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: group("Interchange", [
        ...x12Envelope,
        segment("BIG", 4),
        segment("N1", 4),
        segment("IT1", 7, true),
        segment("TDS", 1),
        ...x12Trailer,
      ]),
      target: group(
        "InvoiceSummary",
        fields(
          scalar("invoiceNumber"),
          scalar("invoiceDate"),
          scalar("purchaseOrder"),
          scalar("billTo"),
          scalar("total", "float"),
          since(
            revision,
            2,
            list("lines", [scalar("sku"), scalar("amount", "float")]),
          ),
        ),
      ),
      sourceOptions: x12Options,
      root: {
        set: {
          invoiceNumber: field("BIG", "e2"),
          invoiceDate: field("BIG", "e1"),
          purchaseOrder: field("BIG", "e4"),
          billTo: field("N1", "e2"),
          total: call("divide", number(field("TDS", "e1")), constant(100)),
        },
        children: fields(
          since(
            revision,
            2,
            scope("lines", {
              from: ["IT1"],
              set: {
                sku: field("e7"),
                amount: call(
                  "multiply",
                  number(field("e2")),
                  number(field("e4")),
                ),
              },
            }),
          ),
        ),
      },
    }),
  samples: [{ name: "Invoice INV-88120", input: invoice810, document: true }],
  cases: [{ name: "Invoice INV-88120", sample: 0, revision: "head" }],
};

// ---------------------------------------------------------------- Shipping notices
const shipNotice856 = x12Document(
  "WEFTWMS",
  "NORTHWINDRET",
  "SH",
  7301,
  `856
BSN*00*ASN-30091*20261104*1640~
TD5**2*FDEG*M*FedEx Ground~
REF*BM*BOL-778120~
LIN*1*VN*MUG-12OZ-BLU~
SN1*1*24*EA~
LIN*2*VN*KETTLE-1L-STL~
SN1*2*10*EA~`,
);
const asn = {
  slug: "x12-856-asn",
  name: "X12 856 ship notice to JSON",
  description:
    "Reads an advance ship notice into the shipment JSON used by the receiving dashboard.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: group("Interchange", [
        ...x12Envelope,
        segment("BSN", 4),
        segment("TD5", 5),
        segment("REF", 2),
        group("Item", [segment("LIN", 3), segment("SN1", 3)], true),
        ...x12Trailer,
      ]),
      target: group(
        "Shipment",
        fields(
          scalar("asnNumber"),
          scalar("shippedAt"),
          scalar("carrier"),
          since(revision, 2, scalar("billOfLading")),
          list("items", [scalar("sku"), scalar("quantity", "float")]),
        ),
      ),
      sourceOptions: x12Options,
      root: {
        set: bindings({
          asnNumber: field("BSN", "e2"),
          shippedAt: concat(field("BSN", "e3"), "T", field("BSN", "e4")),
          carrier: revision >= 2 ? field("TD5", "e5") : field("TD5", "e3"),
          billOfLading: since(revision, 2, field("REF", "e2")),
        }),
        children: [
          scope("items", {
            from: ["Item"],
            set: {
              sku: field("LIN", "e3"),
              quantity: number(field("SN1", "e2")),
            },
          }),
        ],
      },
    }),
  samples: [{ name: "Two-carton ASN", input: shipNotice856, document: true }],
  cases: [
    { name: "Two-carton ASN (carrier code baseline)", sample: 0, revision: 1 },
    { name: "Two-carton ASN", sample: 0, revision: "head" },
  ],
};

const dispatchSource = group("Dispatch", [
  scalar("shipmentId"),
  scalar("carrier"),
  scalar("service"),
  group("consignee", [scalar("name"), scalar("city"), scalar("postalCode")]),
  list("parcels", [scalar("trackingNumber"), scalar("weightKg", "float")]),
]);
const dispatchXml = {
  slug: "dispatch-xml",
  name: "Shipment to XML dispatch notice",
  description:
    "Writes the dispatch notice XML a regional carrier expects when a trailer leaves the dock.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: dispatchSource,
      target: group(
        "DispatchNotice",
        fields(
          scalar("Reference"),
          scalar("Carrier"),
          since(revision, 2, scalar("Service")),
          group("Consignee", [
            scalar("Name"),
            scalar("City"),
            scalar("PostalCode"),
          ]),
          list("Parcel", [scalar("Tracking"), scalar("Weight")]),
        ),
      ),
      targetPath: "dispatch.xml",
      targetOptions: null,
      root: {
        set: bindings({
          Reference: field("shipmentId"),
          Carrier: upper(field("carrier")),
          Service: since(revision, 2, field("service")),
        }),
        children: [
          scope("Consignee", {
            set: {
              Name: field("consignee", "name"),
              City: field("consignee", "city"),
              PostalCode: field("consignee", "postalCode"),
            },
          }),
          scope("Parcel", {
            from: ["parcels"],
            set: {
              Tracking: field("trackingNumber"),
              Weight: fmt(field("weightKg"), "0.0"),
            },
          }),
        ],
      },
    }),
  samples: [],
  cases: [],
};

const manifest = {
  slug: "carrier-manifest",
  name: "Carrier manifest (fixed width)",
  description:
    "Prints the end-of-day parcel manifest in the carrier's fixed-width layout: tracking, consignee city, weight.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: dispatchSource,
      target: group("Manifest", [
        list("Record", [
          scalar("tracking"),
          scalar("city"),
          scalar("weight"),
          ...(revision >= 2 ? [scalar("service")] : []),
        ]),
      ]),
      targetPath: "manifest.txt",
      targetOptions: {
        fixed_width: {
          field_widths: revision >= 2 ? [22, 18, 8, 6] : [22, 18, 8],
          fill_char: " ",
          record_delimiters: true,
          treat_empty_as_absent: true,
        },
      },
      root: {
        children: [
          scope("Record", {
            from: ["parcels"],
            set: bindings({
              tracking: field("trackingNumber"),
              city: upper(top("consignee", "city")),
              weight: fmt(field("weightKg"), "0.00"),
              service: since(revision, 2, top("service")),
            }),
          }),
        ],
      },
    }),
  samples: [],
  cases: [],
};

const trackingCode = (code) =>
  when(
    eq(code, "PU"),
    constant("Picked up"),
    when(
      eq(code, "IT"),
      constant("In transit"),
      when(eq(code, "DL"), constant("Delivered"), constant("Exception")),
    ),
  );
const tracking = {
  slug: "tracking-events",
  name: "Tracking event normalizer",
  description:
    "Normalises carrier scan events into customer-facing tracking statuses.",
  revisions: 3,
  native: (revision) =>
    mapping({
      source: group("Scans", [
        scalar("trackingNumber"),
        list("events", [
          scalar("code"),
          scalar("at"),
          scalar("city"),
          scalar("detail"),
        ]),
      ]),
      target: group(
        "Tracking",
        fields(
          scalar("trackingNumber"),
          since(revision, 3, scalar("delivered", "bool")),
          list(
            "history",
            fields(
              scalar("status"),
              scalar("at"),
              scalar("where"),
              since(revision, 2, scalar("detail")),
            ),
          ),
        ),
      ),
      root: {
        set: bindings({
          trackingNumber: field("trackingNumber"),
          delivered: since(
            revision,
            3,
            call("greater_than", count("events"), constant(3)),
          ),
        }),
        children: [
          scope("history", {
            from: ["events"],
            sortBy: since(revision, 2, field("at")),
            set: bindings({
              status: trackingCode(field("code")),
              at: field("at"),
              where:
                revision >= 2
                  ? call(
                      "substitute_missing",
                      field("city"),
                      constant("Unknown"),
                    )
                  : field("city"),
              detail: since(revision, 2, field("detail")),
            }),
          }),
        ],
      },
    }),
  samples: [
    {
      name: "Delivered parcel",
      input: json({
        trackingNumber: "1Z999AA10123456784",
        events: [
          {
            code: "PU",
            at: "2026-11-02T17:20:00Z",
            city: "Reno",
            detail: "Picked up at dock 4",
          },
          {
            code: "IT",
            at: "2026-11-03T03:05:00Z",
            city: "Salt Lake City",
            detail: "Departed hub",
          },
          { code: "IT", at: "2026-11-03T21:44:00Z", city: "Denver" },
          {
            code: "DL",
            at: "2026-11-04T15:12:00Z",
            city: "Boulder",
            detail: "Left at front desk",
          },
        ],
      }),
    },
    {
      name: "Address exception",
      input: json({
        trackingNumber: "1Z999AA10123456791",
        events: [
          { code: "PU", at: "2026-11-03T16:00:00Z", city: "Reno" },
          {
            code: "EX",
            at: "2026-11-04T10:30:00Z",
            detail: "Address not found",
          },
        ],
      }),
    },
  ],
  cases: [
    { name: "Delivered parcel (first baseline)", sample: 0, revision: 1 },
    { name: "Delivered parcel", sample: 0, revision: "head" },
    { name: "Address exception", sample: 1, revision: "head" },
  ],
  compile: true,
};

// ---------------------------------------------------------------- Inventory sync
const stockCsv = {
  slug: "stock-csv",
  name: "Stock levels CSV import",
  description:
    "Reads the nightly stock-level CSV from the 3PL's SFTP drop into the inventory JSON feed.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: group("Stock", [
        list("Row", [
          scalar("sku"),
          scalar("warehouse"),
          scalar("on_hand", "int"),
          scalar("allocated", "int"),
        ]),
      ]),
      target: group("Inventory", [
        list(
          "items",
          fields(
            scalar("sku"),
            scalar("site"),
            scalar("available", "int"),
            since(revision, 2, scalar("onHand", "int")),
          ),
        ),
      ]),
      sourcePath: "stock.csv",
      sourceOptions: { delimiter: ",", has_header_row: true },
      root: {
        children: [
          scope("items", {
            from: ["Row"],
            set: bindings({
              sku: field("sku"),
              site: upper(field("warehouse")),
              available: call("subtract", field("on_hand"), field("allocated")),
              onHand: since(revision, 2, field("on_hand")),
            }),
          }),
        ],
      },
    }),
  samples: [],
  cases: [],
};

const balanceItem = (sku, site, onHand, allocated, reorderPoint, maxStock) => ({
  sku,
  site,
  onHand,
  allocated,
  reorderPoint,
  maxStock,
});
const balanceInput = {
  snapshotAt: "2026-11-04T06:00:00Z",
  items: [
    balanceItem("MUG-12OZ-BLU", "RNO-1", 340, 28, 120, 600),
    balanceItem("KETTLE-1L-STL", "RNO-1", 22, 18, 40, 150),
    balanceItem("TEA-ASSAM-500G", "ATL-2", 0, 0, 60, 300),
    balanceItem("PLATE-26CM-WHT", "ATL-2", 410, 36, 200, 900),
    balanceItem("BOWL-15CM-WHT", "RNO-1", 95, 90, 100, 400),
  ],
};
const balanceSchema = group("Balances", [
  scalar("snapshotAt"),
  list("items", [
    scalar("sku"),
    scalar("site"),
    scalar("onHand", "int"),
    scalar("allocated", "int"),
    scalar("reorderPoint", "int"),
    scalar("maxStock", "int"),
  ]),
]);
const available = call("subtract", field("onHand"), field("allocated"));

const balance = {
  slug: "warehouse-balance",
  name: "Warehouse balance snapshot",
  description:
    "Publishes available-to-sell stock per SKU and site for the storefronts, hiding sold-out items.",
  revisions: 3,
  native: (revision) =>
    mapping({
      source: balanceSchema,
      target: group("Availability", [
        scalar("asOf"),
        list(
          "skus",
          fields(
            scalar("sku"),
            scalar("site"),
            scalar("available", "int"),
            since(revision, 2, scalar("lowStock", "bool")),
          ),
        ),
      ]),
      root: {
        set: { asOf: field("snapshotAt") },
        children: [
          scope("skus", {
            from: ["items"],
            filter: gt(available, 0),
            sortBy: revision >= 3 ? field("site") : field("sku"),
            sortThen: revision >= 3 ? [[field("sku"), false]] : undefined,
            set: bindings({
              sku: field("sku"),
              site: field("site"),
              available,
              lowStock: since(
                revision,
                2,
                call("less_than", available, field("reorderPoint")),
              ),
            }),
          }),
        ],
      },
    }),
  samples: [{ name: "Morning snapshot", input: json(balanceInput) }],
  cases: [
    { name: "Morning snapshot (first baseline)", sample: 0, revision: 1 },
    { name: "Morning snapshot", sample: 0, revision: "head" },
  ],
  compile: true,
};

const reorder = {
  slug: "reorder-alerts",
  name: "Reorder alerts",
  description:
    "Raises a purchase suggestion for every SKU whose available stock fell below its reorder point.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: balanceSchema,
      target: group("Alerts", [
        list(
          "alerts",
          fields(
            scalar("sku"),
            scalar("site"),
            scalar("suggestedQty", "int"),
            since(revision, 2, scalar("severity")),
          ),
        ),
      ]),
      root: {
        children: [
          scope("alerts", {
            from: ["items"],
            filter: call("less_than", available, field("reorderPoint")),
            set: bindings({
              sku: field("sku"),
              site: field("site"),
              suggestedQty: call("subtract", field("maxStock"), available),
              severity: since(
                revision,
                2,
                when(
                  call("less_or_equal", available, constant(0)),
                  constant("stock-out"),
                  constant("low"),
                ),
              ),
            }),
          }),
        ],
      },
    }),
  samples: [{ name: "Morning snapshot", input: json(balanceInput) }],
  cases: [{ name: "Morning snapshot", sample: 0, revision: "head" }],
  compile: true,
};

const invrpt = `UNB+UNOC:3+NORDLAGER+WEFTWMS+261103:0600+7781'
UNH+1+INVRPT:D:96A:UN'
BGM+35+INV-20261103+9'
DTM+137:20261103:102'
LIN+1++7310865004703:EN'
QTY+145:120'
LIN+2++7310865004710:EN'
QTY+145:48'
UNT+8+1'
UNZ+1+7781'
`;
const inventoryReport = {
  slug: "edifact-invrpt",
  name: "EDIFACT INVRPT to stock",
  description:
    "Reads a Nordic warehouse partner's EDIFACT inventory report into the inventory JSON feed.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: group("Interchange", [
        edifactSegment("UNB", [2, 1, 1, 2, 1]),
        edifactSegment("UNH", [1, 4]),
        edifactSegment("BGM", [1, 1, 1]),
        edifactSegment("DTM", [3]),
        group(
          "Line",
          [edifactSegment("LIN", [1, 1, 2]), edifactSegment("QTY", [2])],
          true,
        ),
        edifactSegment("UNT", [1, 1]),
        edifactSegment("UNZ", [1, 1]),
      ]),
      target: group(
        "Inventory",
        fields(
          scalar("report"),
          scalar("sender"),
          since(revision, 2, scalar("date")),
          list("items", [scalar("gtin"), scalar("quantity", "float")]),
        ),
      ),
      sourceOptions: edifactOptions,
      root: {
        set: bindings({
          report: field("BGM", "e2"),
          sender: field("UNB", "e2"),
          date: since(revision, 2, field("DTM", "e1", "c2")),
        }),
        children: [
          scope("items", {
            from: ["Line"],
            set: {
              gtin: field("LIN", "e3", "c1"),
              quantity: number(field("QTY", "e1", "c2")),
            },
          }),
        ],
      },
    }),
  samples: [{ name: "Two-line report", input: invrpt, document: true }],
  cases: [{ name: "Two-line report", sample: 0, revision: "head" }],
};

// ---------------------------------------------------------------- Partner onboarding
const partnerSchema = group("Partner", [
  scalar("legalName"),
  scalar("tradingName"),
  scalar("email"),
  scalar("country"),
  scalar("vatNumber"),
  list("contacts", [
    scalar("first"),
    scalar("last"),
    scalar("role"),
    scalar("email"),
  ]),
]);
const partner = (
  legalName,
  tradingName,
  email,
  country,
  vatNumber,
  contacts,
) => ({
  legalName,
  tradingName,
  email,
  country,
  vatNumber,
  contacts,
});
const contact = (first, last, role, email) => ({ first, last, role, email });
const harbor = partner(
  "  Harbor & Pine Hospitality LLC ",
  "harbor & pine",
  "AP@HarborPine.example",
  "us",
  undefined,
  [
    contact(
      "Dana",
      "Whitfield",
      "Accounts payable",
      "dana.whitfield@harborpine.example",
    ),
    contact("Luis", "Ortega", "Receiving", "luis.ortega@harborpine.example"),
  ],
);
const lindner = partner(
  "Kaffeehaus Lindner GmbH",
  "Lindner",
  "einkauf@lindner.example",
  "de",
  "DE298765432",
  [contact("Jana", "Lindner", "Owner", "jana@lindner.example")],
);

const partnerProfile = {
  slug: "partner-profile",
  name: "Partner profile normalizer",
  description:
    "Cleans up a new trading partner's self-service sign-up form into the canonical partner record.",
  revisions: 3,
  native: (revision) =>
    mapping({
      source: partnerSchema,
      target: group(
        "PartnerRecord",
        fields(
          scalar("partnerCode"),
          scalar("name"),
          scalar("displayName"),
          scalar("email"),
          scalar("country"),
          since(revision, 2, scalar("taxStatus")),
          since(revision, 3, scalar("contactCount", "int")),
        ),
      ),
      root: {
        set: bindings({
          partnerCode: concat(
            parameter("prefix"),
            "-",
            upper(
              call(
                "left",
                call("normalize_space", field("tradingName")),
                constant(4),
              ),
            ),
          ),
          name: call("normalize_space", field("legalName")),
          displayName:
            revision >= 2 ? upper(field("tradingName")) : field("tradingName"),
          email: call("lower", field("email")),
          country: upper(field("country")),
          taxStatus: since(
            revision,
            2,
            when(
              call("exists", field("vatNumber")),
              concat("VAT ", field("vatNumber")),
              constant("No VAT number on file"),
            ),
          ),
          contactCount: since(revision, 3, count("contacts")),
        }),
      },
    }),
  samples: [
    {
      name: "US cafe group",
      input: json(harbor),
      parameters: [{ name: "prefix", kind: "string", value: "P" }],
    },
    {
      name: "German roaster",
      input: json(lindner),
      parameters: [{ name: "prefix", kind: "string", value: "EU" }],
    },
  ],
  cases: [
    { name: "US cafe group (first baseline)", sample: 0, revision: 1 },
    { name: "US cafe group", sample: 0, revision: "head" },
    { name: "German roaster", sample: 1, revision: "head" },
    {
      name: "German roaster (outdated tax text)",
      sample: 1,
      revision: "head",
      stale: true,
    },
  ],
  compile: true,
};

const partnerXml = {
  slug: "partner-xml",
  name: "Trading partner XML import",
  description:
    "Imports partner master data from the procurement system's XML export.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: group("PartnerExport", [
        list("Partner", [
          scalar("Code"),
          scalar("Name"),
          scalar("Country"),
          scalar("Status"),
        ]),
      ]),
      target: group("Partners", [
        list(
          "partners",
          fields(
            scalar("code"),
            scalar("name"),
            scalar("country"),
            since(revision, 2, scalar("active", "bool")),
          ),
        ),
      ]),
      sourcePath: "partners.xml",
      sourceOptions: null,
      root: {
        children: [
          scope("partners", {
            from: ["Partner"],
            filter: since(
              revision,
              2,
              call("not_equal", field("Status"), constant("deleted")),
            ),
            set: bindings({
              code: field("Code"),
              name: field("Name"),
              country: upper(field("Country")),
              active: since(revision, 2, eq(field("Status"), "active")),
            }),
          }),
        ],
      },
    }),
  samples: [],
  cases: [],
};

const directory = {
  slug: "contact-directory",
  name: "Contact directory",
  description:
    "Builds the partner contact directory for the support desk, sorted by surname.",
  revisions: 2,
  native: (revision) =>
    mapping({
      source: partnerSchema,
      target: group("Directory", [
        scalar("company"),
        list(
          "people",
          fields(
            scalar("display"),
            scalar("role"),
            scalar("email"),
            since(revision, 2, scalar("initials")),
          ),
        ),
      ]),
      root: {
        set: { company: call("normalize_space", field("legalName")) },
        children: [
          scope("people", {
            from: ["contacts"],
            sortBy: field("last"),
            set: bindings({
              display: concat(field("last"), ", ", field("first")),
              role: field("role"),
              email: call("lower", field("email")),
              initials: since(
                revision,
                2,
                concat(
                  call("left", field("first"), constant(1)),
                  call("left", field("last"), constant(1)),
                ),
              ),
            }),
          }),
        ],
      },
    }),
  samples: [{ name: "US cafe group", input: json(harbor) }],
  cases: [{ name: "US cafe group", sample: 0, revision: "head" }],
};

// Revision 2 calls a function Ferrule does not have, so it is committed as decoded-invalid
// history with diagnostics; revision 3 fixes it.
const checklist = {
  slug: "onboarding-checklist",
  name: "Onboarding checklist (draft)",
  description:
    "Work in progress: derives the onboarding checklist for a new partner. Revision 2 has a deliberate mistake.",
  revisions: 3,
  native: (revision) =>
    mapping({
      source: partnerSchema,
      target: group(
        "Checklist",
        fields(
          scalar("partner"),
          scalar("needsTaxForm", "bool"),
          scalar("contactsReady", "bool"),
          since(revision, 3, scalar("country")),
        ),
      ),
      root: {
        set: bindings({
          partner: field("tradingName"),
          needsTaxForm: call(
            revision === 2 ? "has_value" : "exists",
            field("vatNumber"),
          ),
          contactsReady: gt(count("contacts"), 0),
          country: since(revision, 3, upper(field("country"))),
        }),
      },
    }),
  samples: [{ name: "German roaster", input: json(lindner) }],
  cases: [],
};

// ---------------------------------------------------------------- Projects
export const projects = [
  {
    slug: "order-fulfilment",
    name: "Order fulfilment",
    description:
      "Storefront and retailer orders into the warehouse: pick lists, routing, backorders and acknowledgements.",
    automation: {
      requirePassingRegression: false,
      errorRatePercent: 5,
      minimumRequests: 20,
      windowSeconds: 300,
    },
    mappings: [
      pickList,
      purchaseOrderFromX12,
      routing,
      backorders,
      acknowledgement,
    ],
  },
  {
    slug: "invoicing",
    name: "Invoicing",
    description:
      "Shipment pricing, ledger postings and invoice documents for finance and customers.",
    automation: {
      requirePassingRegression: true,
      errorRatePercent: 2,
      minimumRequests: 50,
      windowSeconds: 600,
    },
    mappings: [invoiceFromShipment, ledger, invoiceCsv, invoiceSummary],
  },
  {
    slug: "shipping-notices",
    name: "Shipping notices",
    description:
      "Advance ship notices, carrier documents and tracking updates.",
    automation: {
      requirePassingRegression: false,
      errorRatePercent: 10,
      minimumRequests: 10,
      windowSeconds: 120,
    },
    mappings: [asn, dispatchXml, manifest, tracking],
  },
  {
    slug: "inventory-sync",
    name: "Inventory sync",
    description:
      "Stock levels between the 3PL warehouses, partners and the storefronts.",
    automation: {
      requirePassingRegression: false,
      errorRatePercent: 5,
      minimumRequests: 30,
      windowSeconds: 900,
    },
    mappings: [stockCsv, balance, reorder, inventoryReport],
    disableAutomation: ["reorder-alerts"],
  },
  {
    slug: "partner-onboarding",
    name: "Partner onboarding",
    description:
      "Sign-up forms, partner master data and contact directories for new trading partners.",
    automation: {
      requirePassingRegression: false,
      errorRatePercent: 5,
      minimumRequests: 20,
      windowSeconds: 300,
    },
    mappings: [partnerProfile, partnerXml, directory, checklist],
  },
];

// Ferrule's own examples, uploaded unchanged into the default project when a Ferrule checkout's
// examples directory is supplied.
export const ferruleExamples = [
  {
    slug: "ferrule-codegen",
    file: "codegen/project.json",
    input: "codegen/input.json",
    name: "Order invoices (Ferrule example)",
    description:
      "Filters zero-value orders, sorts them, numbers each invoice and formats its display text.",
    originalFilename: "order-invoices.native.json",
  },
  {
    slug: "ferrule-canvas",
    file: "canvas-density/project.json",
    name: "Canvas presentation (Ferrule example)",
    description:
      "Exercises the workbench: fan-out, Exists/Not/If rules, long and Unicode constants, scrolling.",
    originalFilename: "canvas-presentation.native.json",
  },
  {
    slug: "ferrule-sequences",
    file: "performance/scalar-sequences/project-capture.json",
    name: "Generated sequence (Ferrule example)",
    description:
      "A generated filter-map sequence with user functions and a captured scalar.",
    originalFilename: "scalar-sequence.native.json",
  },
];

// Example questions for the chat history (answered by the instance's local model).
export const conversations = [
  {
    slug: "pick-list-chat",
    project: "order-fulfilment",
    mapping: "pick-list",
    text: "Why are some order lines missing from the pick list?",
  },
  {
    slug: "invoice-chat",
    project: "invoicing",
    mapping: "invoice-from-shipment",
    text: "How is the line amount calculated, and where does the discount apply?",
  },
  { slug: "workspace-chat", text: "Which mappings read X12 documents?" },
];
