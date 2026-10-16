import type { UserRef } from './types';

export type IntegrationProvider = 'GitHubWebhook' | 'GitHubApp' | 'Generic';
export type IntegrationStatus = 'Active' | 'Disabled';
export type RepositoryStatus = 'Pending' | 'Authorized' | 'Ignored';
export type DeliveryStatus = 'Received' | 'Processed' | 'Ignored' | 'Failed';
export type RunStatus = 'Queued' | 'InProgress' | 'Completed';
export type RunConclusion = 'Success' | 'Failure' | 'Cancelled' | 'TimedOut' | 'Skipped' | 'ActionRequired' | 'Neutral' | 'Stale' | 'Unknown';
export type DeploymentStatus = 'Pending' | 'Queued' | 'InProgress' | 'Success' | 'Failure' | 'Error' | 'Inactive';
export type EventSource = 'GitHub' | 'Api';
export type IncidentSeverity = 'Low' | 'Medium' | 'High' | 'Critical';
export type IncidentStatus = 'Open' | 'Investigating' | 'Mitigated' | 'Resolved';
export type IncidentSource = 'Manual' | 'PipelineFailure' | 'DeploymentFailure' | 'ErrorGroup';
export type LogLevel = 'Trace' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Critical';
export type AiAnalysisKind = 'LogAnalysis' | 'PipelineFailure' | 'IncidentSummary' | 'Assistant' | 'ActivitySummary';

export interface Integration {
  id: string;
  provider: IntegrationProvider;
  name: string;
  status: IntegrationStatus;
  createdAt: string;
  lastEventAt: string | null;
  verifiedAt: string | null;
  lastErrorAt: string | null;
  lastError: string | null;
  eventCount: number;
  webhookUrl: string | null;
  repositoryCount: number;
  failedDeliveries24h: number;
  externalAccount: string | null;
}

export interface IntegrationSecret {
  integration: Integration;
  webhookUrl: string;
  secret: string;
  instructions: string[];
}

export interface Repository {
  id: string;
  integrationId: string;
  fullName: string;
  defaultBranch: string | null;
  htmlUrl: string | null;
  status: RepositoryStatus;
  projectId: string | null;
  projectName: string | null;
  firstSeenAt: string;
  lastEventAt: string;
}

export interface Delivery {
  id: string;
  deliveryId: string;
  eventType: string;
  action: string | null;
  repositoryFullName: string | null;
  status: DeliveryStatus;
  attempts: number;
  error: string | null;
  receivedAt: string;
  processedAt: string | null;
  nextAttemptAt: string | null;
  payloadAvailable: boolean;
}

export interface IngestKey {
  id: string;
  name: string;
  prefix: string;
  scopes: string;
  projectId: string | null;
  createdAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
}

export interface PipelineRun {
  id: string;
  pipelineId: string;
  pipelineName: string;
  projectId: string | null;
  repositoryFullName: string | null;
  externalRunId: string;
  runNumber: number | null;
  attempt: number;
  branch: string | null;
  commitSha: string | null;
  trigger: string | null;
  actor: string | null;
  status: RunStatus;
  conclusion: RunConclusion | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  durationSeconds: number | null;
  htmlUrl: string | null;
  source: EventSource;
}

export interface Deployment {
  id: string;
  environment: string;
  status: DeploymentStatus;
  ref: string | null;
  commitSha: string | null;
  description: string | null;
  creator: string | null;
  projectId: string | null;
  repositoryFullName: string | null;
  url: string | null;
  createdAt: string;
  updatedAt: string;
  finishedAt: string | null;
  durationSeconds: number | null;
  source: EventSource;
}

export interface CodeEvent {
  id: string;
  kind: 'Push' | 'PullRequest';
  title: string;
  author: string | null;
  branch: string | null;
  url: string | null;
  state: string | null;
  commitCount: number;
  repositoryFullName: string;
  occurredAt: string;
}

export interface PipelineHealth {
  pipelineId: string;
  name: string;
  runs: number;
  failures: number;
  successRate: number | null;
  avgDurationSeconds: number | null;
  lastConclusion: RunConclusion | null;
  lastRunAt: string | null;
}

export interface OperationsSummary {
  runs7d: number;
  failedRuns7d: number;
  successRate7d: number | null;
  avgDurationSeconds7d: number | null;
  deployments7d: number;
  failedDeployments7d: number;
  recentDeployments: Deployment[];
  recentFailures: PipelineRun[];
  pipelines: PipelineHealth[];
  activeIntegrations: number;
  integrationsWithErrors: number;
  pendingRepositories: number;
  openIncidents: number;
  criticalOpenIncidents: number;
  hasOperationalData: boolean;
}

export interface LogEvent {
  id: string;
  projectId: string | null;
  source: string;
  level: LogLevel;
  message: string;
  exception: string | null;
  fingerprint: string;
  environment: string | null;
  timestamp: string;
}

export interface ErrorGroup {
  fingerprint: string;
  sampleMessage: string;
  sampleException: string | null;
  source: string;
  maxLevel: LogLevel;
  count: number;
  firstSeen: string;
  lastSeen: string;
}

export interface IncidentSummary {
  id: string;
  number: number;
  key: string;
  title: string;
  severity: IncidentSeverity;
  status: IncidentStatus;
  source: IncidentSource;
  projectId: string | null;
  projectName: string | null;
  assignee: UserRef | null;
  occurrenceCount: number;
  createdAt: string;
  updatedAt: string;
  lastOccurredAt: string;
  resolvedAt: string | null;
}

export interface IncidentUpdate {
  id: string;
  kind: 'Note' | 'StatusChange' | 'System';
  body: string;
  author: UserRef | null;
  at: string;
}

export interface IncidentDetail {
  incident: IncidentSummary;
  description: string | null;
  logExcerpt: string | null;
  pipelineRunId: string | null;
  deploymentId: string | null;
  updates: IncidentUpdate[];
  version: number;
  canEdit: boolean;
}

export interface AnalysisResult {
  requestId: string;
  provider: string;
  model: string;
  summary: string;
  classification: { category: string; severity: string; confidence: number | null };
  facts: { statement: string; evidence: string | null; line: number | null }[];
  hypotheses: { statement: string; rationale: string; confidence: number | null }[];
  recommendations: { action: string; rationale: string; requiresApproval: boolean }[];
  patterns: { signature: string; occurrences: number; example: string }[];
  redactions: { count: number; kinds: Record<string, number> };
  truncated: boolean;
  latencyMs: number;
  warnings: string[];
}

export interface AssistantResult {
  answer: string;
  citations: { id: string; type: string; title: string }[];
  grounded: boolean;
  provider: string;
  model: string;
  redactions: { count: number; kinds: Record<string, number> };
  warnings: string[];
}

export interface ActivitySummaryResult {
  summary: string;
  highlights: string[];
  risks: string[];
  provider: string;
  model: string;
  warnings: string[];
}

export interface AiAnalysis {
  id: string;
  kind: AiAnalysisKind;
  status: 'Succeeded' | 'Failed';
  subject: string | null;
  projectId: string | null;
  incidentId: string | null;
  provider: string | null;
  model: string | null;
  errorCode: string | null;
  inputChars: number;
  latencyMs: number | null;
  createdAt: string;
  requestedBy: string | null;
  result: unknown;
}

export interface AiStatus {
  reachable: boolean;
  provider: string | null;
  model: string | null;
  externalProviderConfigured: boolean;
  mode: 'deterministic' | 'model' | 'unavailable';
}

export interface AuditEntry {
  id: string;
  action: string;
  targetType: string;
  targetId: string | null;
  actorName: string | null;
  actorEmail: string | null;
  details: Record<string, string | null>;
  ipAddress: string | null;
  at: string;
}

export interface AdminOverview {
  members: number;
  projects: number;
  archivedProjects: number;
  activeIntegrations: number;
  pendingRepositories: number;
  failedDeliveries24h: number;
  activeIngestKeys: number;
  logEvents7d: number;
  aiAnalyses30d: number;
  failedAiAnalyses30d: number;
  ai: AiStatus;
  retention: { auditRetentionDays: number; logRetentionDays: number; webhookPayloadRetentionDays: number; readNotificationRetentionDays: number };
  oldestAuditEntry: string | null;
}
