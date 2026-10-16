import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, qs } from './client';
import type {
  AdminOverview,
  AiAnalysis,
  AiAnalysisKind,
  AiStatus,
  AuditEntry,
  CodeEvent,
  Delivery,
  DeliveryStatus,
  Deployment,
  DeploymentStatus,
  ErrorGroup,
  IncidentDetail,
  IncidentSeverity,
  IncidentStatus,
  IncidentSummary,
  IngestKey,
  Integration,
  IntegrationSecret,
  IntegrationStatus,
  LogEvent,
  LogLevel,
  OperationsSummary,
  PipelineRun,
  Repository,
  RepositoryStatus,
} from './opsTypes';
import type { PagedResult } from './types';

const org = (orgId: string) => `/api/orgs/${orgId}`;

function useInvalidate(orgId: string, ...scopes: string[]) {
  const qc = useQueryClient();
  return () => {
    for (const scope of scopes) void qc.invalidateQueries({ queryKey: ['org', orgId, scope] });
    void qc.invalidateQueries({ queryKey: ['org', orgId, 'dashboard'] });
    void qc.invalidateQueries({ queryKey: ['org', orgId, 'ops-summary'] });
  };
}

// ---------- Operations ----------

export function useOperationsSummary(orgId: string) {
  return useQuery({
    queryKey: ['org', orgId, 'ops-summary'],
    refetchInterval: 60_000,
    queryFn: ({ signal }) => api.get<OperationsSummary>(`${org(orgId)}/operations/summary`, signal),
  });
}

export function usePipelineRuns(orgId: string, params: { projectId?: string; branch?: string; failed?: boolean; page?: number }) {
  return useQuery({
    queryKey: ['org', orgId, 'runs', params],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => api.get<PagedResult<PipelineRun>>(`${org(orgId)}/pipeline-runs${qs({ ...params, pageSize: 25 })}`, signal),
  });
}

export function useDeployments(orgId: string, params: { projectId?: string; environment?: string; status?: DeploymentStatus; page?: number }) {
  return useQuery({
    queryKey: ['org', orgId, 'deployments', params],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => api.get<PagedResult<Deployment>>(`${org(orgId)}/deployments${qs({ ...params, pageSize: 25 })}`, signal),
  });
}

export function useEnvironments(orgId: string) {
  return useQuery({
    queryKey: ['org', orgId, 'deployments', 'environments'],
    queryFn: ({ signal }) => api.get<string[]>(`${org(orgId)}/deployments/environments`, signal),
  });
}

export function useCodeEvents(orgId: string, page: number) {
  return useQuery({
    queryKey: ['org', orgId, 'code-events', page],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => api.get<PagedResult<CodeEvent>>(`${org(orgId)}/code-events${qs({ page, pageSize: 25 })}`, signal),
  });
}

export function useLogs(orgId: string, params: { minLevel?: LogLevel; q?: string; fingerprint?: string; page?: number }) {
  return useQuery({
    queryKey: ['org', orgId, 'logs', params],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => api.get<PagedResult<LogEvent>>(`${org(orgId)}/logs${qs({ ...params, pageSize: 50 })}`, signal),
  });
}

export function useErrorGroups(orgId: string) {
  return useQuery({
    queryKey: ['org', orgId, 'logs', 'groups'],
    queryFn: ({ signal }) => api.get<ErrorGroup[]>(`${org(orgId)}/logs/groups`, signal),
  });
}

// ---------- Integrations ----------

export function useIntegrations(orgId: string, enabled = true) {
  return useQuery({
    queryKey: ['org', orgId, 'integrations'],
    enabled,
    queryFn: ({ signal }) => api.get<Integration[]>(`${org(orgId)}/integrations`, signal),
  });
}

export function useIntegrationMutations(orgId: string) {
  const invalidate = useInvalidate(orgId, 'integrations', 'repositories');
  return {
    create: useMutation({ mutationFn: (name: string) => api.post<IntegrationSecret>(`${org(orgId)}/integrations/github`, { name }), onSuccess: invalidate }),
    rotate: useMutation({ mutationFn: (id: string) => api.post<IntegrationSecret>(`${org(orgId)}/integrations/${id}/rotate-secret`), onSuccess: invalidate }),
    setStatus: useMutation({
      mutationFn: ({ id, status }: { id: string; status: IntegrationStatus }) => api.put<Integration>(`${org(orgId)}/integrations/${id}/status`, { status }),
      onSuccess: invalidate,
    }),
    remove: useMutation({ mutationFn: (id: string) => api.del(`${org(orgId)}/integrations/${id}`), onSuccess: invalidate }),
  };
}

export function useDeliveries(orgId: string, integrationId: string | null, status?: DeliveryStatus) {
  return useQuery({
    queryKey: ['org', orgId, 'integrations', integrationId, 'deliveries', status ?? 'all'],
    enabled: !!integrationId,
    queryFn: ({ signal }) =>
      api.get<PagedResult<Delivery>>(`${org(orgId)}/integrations/${integrationId}/deliveries${qs({ status, pageSize: 50 })}`, signal),
  });
}

export function useRetryDelivery(orgId: string, integrationId: string) {
  const invalidate = useInvalidate(orgId, 'integrations', 'runs', 'deployments', 'incidents');
  return useMutation({
    mutationFn: (deliveryId: string) => api.post<Delivery>(`${org(orgId)}/integrations/${integrationId}/deliveries/${deliveryId}/retry`),
    onSuccess: invalidate,
  });
}

export function useRepositories(orgId: string) {
  return useQuery({
    queryKey: ['org', orgId, 'repositories'],
    queryFn: ({ signal }) => api.get<Repository[]>(`${org(orgId)}/repositories`, signal),
  });
}

export function useConfigureRepository(orgId: string) {
  const invalidate = useInvalidate(orgId, 'repositories', 'integrations');
  return useMutation({
    mutationFn: ({ id, status, projectId }: { id: string; status: RepositoryStatus; projectId: string | null }) =>
      api.put<Repository>(`${org(orgId)}/repositories/${id}`, { status, projectId }),
    onSuccess: invalidate,
  });
}

export function useIngestKeys(orgId: string, enabled = true) {
  return useQuery({
    queryKey: ['org', orgId, 'ingest-keys'],
    enabled,
    queryFn: ({ signal }) => api.get<IngestKey[]>(`${org(orgId)}/ingest-keys`, signal),
  });
}

export function useIngestKeyMutations(orgId: string) {
  const invalidate = useInvalidate(orgId, 'ingest-keys');
  return {
    create: useMutation({
      mutationFn: (body: { name: string; projectId: string | null; scopes: string }) =>
        api.post<{ key: IngestKey; secret: string }>(`${org(orgId)}/ingest-keys`, body),
      onSuccess: invalidate,
    }),
    revoke: useMutation({ mutationFn: (id: string) => api.del(`${org(orgId)}/ingest-keys/${id}`), onSuccess: invalidate }),
  };
}

// ---------- Incidents ----------

export function useIncidents(orgId: string, params: { status?: IncidentStatus; open?: boolean; severity?: IncidentSeverity; q?: string; page?: number }) {
  return useQuery({
    queryKey: ['org', orgId, 'incidents', params],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => api.get<PagedResult<IncidentSummary>>(`${org(orgId)}/incidents${qs({ ...params, pageSize: 25 })}`, signal),
  });
}

export function useIncident(orgId: string, incidentId: string) {
  return useQuery({
    queryKey: ['org', orgId, 'incidents', 'detail', incidentId],
    queryFn: ({ signal }) => api.get<IncidentDetail>(`${org(orgId)}/incidents/${incidentId}`, signal),
  });
}

export function useIncidentMutations(orgId: string, incidentId?: string) {
  const invalidate = useInvalidate(orgId, 'incidents', 'ai');
  const base = `${org(orgId)}/incidents`;
  return {
    create: useMutation({
      mutationFn: (body: { title: string; severity: IncidentSeverity; projectId: string | null; description?: string; logExcerpt?: string }) =>
        api.post<IncidentDetail>(base, body),
      onSuccess: invalidate,
    }),
    update: useMutation({
      mutationFn: (body: { title: string; description: string | null; severity: IncidentSeverity; status: IncidentStatus; assigneeId: string | null; version: number }) =>
        api.put<IncidentDetail>(`${base}/${incidentId}`, body),
      onSuccess: invalidate,
    }),
    note: useMutation({ mutationFn: (body: string) => api.post<IncidentDetail>(`${base}/${incidentId}/notes`, { body }), onSuccess: invalidate }),
    attachLog: useMutation({ mutationFn: (log: string) => api.post<IncidentDetail>(`${base}/${incidentId}/log`, { log }), onSuccess: invalidate }),
    analyze: useMutation({ mutationFn: () => api.post<AiAnalysis>(`${base}/${incidentId}/analyze`), onSettled: invalidate }),
  };
}

// ---------- AI ----------

export function useAiStatus(orgId: string) {
  return useQuery({
    queryKey: ['org', orgId, 'ai', 'status'],
    staleTime: 30_000,
    queryFn: ({ signal }) => api.get<AiStatus>(`${org(orgId)}/ai/status`, signal),
  });
}

export function useAiAnalyses(orgId: string, params: { kind?: AiAnalysisKind; incidentId?: string; projectId?: string; page?: number }) {
  return useQuery({
    queryKey: ['org', orgId, 'ai', 'analyses', params],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => api.get<PagedResult<AiAnalysis>>(`${org(orgId)}/ai/analyses${qs({ ...params, pageSize: 20 })}`, signal),
  });
}

export function useAiMutations(orgId: string) {
  const invalidate = useInvalidate(orgId, 'ai');
  return {
    analyzeLogs: useMutation({
      mutationFn: (body: { content: string; kind?: string; projectId?: string | null; environment?: string; title?: string }) =>
        api.post<AiAnalysis>(`${org(orgId)}/ai/analyses`, body),
      onSettled: invalidate,
    }),
    analyzeGroup: useMutation({
      mutationFn: (fingerprint: string) => api.post<AiAnalysis>(`${org(orgId)}/ai/analyses/error-group/${fingerprint}`),
      onSettled: invalidate,
    }),
    ask: useMutation({
      mutationFn: (body: { question: string; projectId?: string | null }) => api.post<AiAnalysis>(`${org(orgId)}/ai/assistant`, body),
      onSettled: invalidate,
    }),
    summarizeProject: useMutation({
      mutationFn: ({ projectId, days }: { projectId: string; days: number }) =>
        api.post<AiAnalysis>(`${org(orgId)}/projects/${projectId}/activity-summary${qs({ days })}`),
      onSettled: invalidate,
    }),
  };
}

// ---------- Administration ----------

export function useAdminOverview(orgId: string) {
  return useQuery({
    queryKey: ['org', orgId, 'admin', 'overview'],
    queryFn: ({ signal }) => api.get<AdminOverview>(`${org(orgId)}/admin/overview`, signal),
  });
}

export function useAudit(orgId: string, params: { action?: string; page?: number }) {
  return useQuery({
    queryKey: ['org', orgId, 'admin', 'audit', params],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => api.get<PagedResult<AuditEntry>>(`${org(orgId)}/admin/audit${qs({ ...params, pageSize: 50 })}`, signal),
  });
}
