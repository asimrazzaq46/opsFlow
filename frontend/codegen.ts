import type { CodegenConfig } from '@graphql-codegen/cli';

// Schema SDL is exported from the backend by GraphQlSchemaTests (OPSFLOW_UPDATE_GRAPHQL_SCHEMA=1); `npm run graphql:codegen` regenerates the typed operations.
const config: CodegenConfig = {
  schema: 'graphql/schema.graphql',
  documents: 'src/app/core/graphql/operations/*.graphql',
  generates: {
    'src/app/core/graphql/generated.ts': {
      plugins: ['typescript-operations', 'typed-document-node'],
      config: { enumsAsTypes: true, useTypeImports: true, strictScalars: true, scalars: { UUID: 'string', DateTime: 'string', LocalDate: 'string', Long: 'number' }, skipTypename: true },
    },
  },
};

export default config;
