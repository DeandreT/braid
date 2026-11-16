# .NET tests

Keep tests and feature-specific helpers together by responsibility:

- `Administration`, `Automation`, `Catalog`, `Hosting`, `Identity`: application access and workflows.
- `Chat`, `Chat/Providers`: conversations, tools and model providers.
- `Deployment`: releases, provisioning and local sandbox behavior.
- `Execution/{Builds,Endpoints,Ferrule,Preview,Regression,Runtime,Workers}`: execution services and their adapters.
- `Formats/{Csv,Documents,Edi,Xml}`: document formats and comparison rules.
- `Mappings/{Creation,Editing,Ingestion,Interchange,Metadata}`: mapping lifecycle operations.
- `Reports`, `Specifications`: revision reports and specification intake.
- `Storage`, `Storage/Maintenance`: private objects, publication and cleanup.
- `Documentation`: help and user-guide behavior.
- `Shared`: cross-feature test infrastructure and its own tests.

Keep all parts of a partial class together. Directory names do not change test
namespaces or test selection. Shared fixture data stays under `Fixtures`.
