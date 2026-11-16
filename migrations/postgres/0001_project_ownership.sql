CREATE SCHEMA braid AUTHORIZATION braid_migrator;
REVOKE ALL ON SCHEMA braid FROM PUBLIC;
REVOKE ALL ON SCHEMA braid FROM braid_runtime;
GRANT USAGE ON SCHEMA braid TO braid_runtime;

CREATE TABLE braid.schema_migrations (
    version integer PRIMARY KEY CHECK (version > 0),
    checksum text NOT NULL CHECK (checksum ~ '^[0-9a-f]{64}$'),
    applied_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE braid.organizations (
    id uuid PRIMARY KEY CHECK (id <> '00000000-0000-0000-0000-000000000000'),
    name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 200 AND char_length(btrim(name)) > 0),
    created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE braid.principals (
    id uuid PRIMARY KEY CHECK (id <> '00000000-0000-0000-0000-000000000000'),
    active boolean NOT NULL DEFAULT true,
    created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE braid.organization_memberships (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id) ON DELETE RESTRICT,
    principal_id uuid NOT NULL REFERENCES braid.principals(id) ON DELETE RESTRICT,
    active boolean NOT NULL DEFAULT true,
    PRIMARY KEY (organization_id, principal_id)
);

CREATE TABLE braid.projects (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id) ON DELETE RESTRICT,
    id uuid NOT NULL CHECK (id <> '00000000-0000-0000-0000-000000000000'),
    name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 200 AND char_length(btrim(name)) > 0),
    description text NOT NULL DEFAULT '' CHECK (char_length(description) <= 4000),
    creator_principal_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    PRIMARY KEY (organization_id, id),
    FOREIGN KEY (organization_id, creator_principal_id)
        REFERENCES braid.organization_memberships(organization_id, principal_id) ON DELETE RESTRICT,
    CHECK (updated_at >= created_at)
);

CREATE TABLE braid.project_memberships (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    principal_id uuid NOT NULL,
    role text NOT NULL CHECK (role IN ('viewer', 'runner', 'publisher', 'administrator')),
    active boolean NOT NULL DEFAULT true,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    PRIMARY KEY (organization_id, project_id, principal_id),
    FOREIGN KEY (organization_id, project_id)
        REFERENCES braid.projects(organization_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (organization_id, principal_id)
        REFERENCES braid.organization_memberships(organization_id, principal_id) ON DELETE RESTRICT
);

CREATE INDEX project_memberships_principal_projects
    ON braid.project_memberships (organization_id, principal_id, project_id) WHERE active;

ALTER TABLE braid.organizations ENABLE ROW LEVEL SECURITY;
CREATE POLICY organizations_organization_scope ON braid.organizations
    USING (id = NULLIF(current_setting('braid.organization_id', true), '')::uuid)
    WITH CHECK (id = NULLIF(current_setting('braid.organization_id', true), '')::uuid);

ALTER TABLE braid.organization_memberships ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_memberships_organization_scope ON braid.organization_memberships
    USING (organization_id = NULLIF(current_setting('braid.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('braid.organization_id', true), '')::uuid);

ALTER TABLE braid.projects ENABLE ROW LEVEL SECURITY;
CREATE POLICY projects_organization_scope ON braid.projects
    USING (organization_id = NULLIF(current_setting('braid.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('braid.organization_id', true), '')::uuid);

ALTER TABLE braid.project_memberships ENABLE ROW LEVEL SECURITY;
CREATE POLICY project_memberships_organization_scope ON braid.project_memberships
    USING (organization_id = NULLIF(current_setting('braid.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('braid.organization_id', true), '')::uuid);

REVOKE ALL ON ALL TABLES IN SCHEMA braid FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA braid FROM braid_runtime;
GRANT SELECT ON braid.organizations, braid.principals, braid.organization_memberships,
    braid.projects, braid.project_memberships TO braid_runtime;
