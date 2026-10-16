// Mirrors the DTOs of the .NET API (System.Text.Json web defaults: camelCase, enums as strings).

export type OrgRole = 'Viewer' | 'Developer' | 'Admin' | 'Owner';
export type ProjectRole = 'Member' | 'Lead';
export type ProjectStatus = 'Active' | 'Archived';
export type StatusCategory = 'Todo' | 'InProgress' | 'Done' | 'Cancelled';
export type WorkItemType = 'Task' | 'Issue' | 'Subtask';
export type Priority = 'Low' | 'Medium' | 'High' | 'Critical';
export type InvitationStatus = 'Pending' | 'Accepted' | 'Revoked' | 'Expired';

export const ORG_ROLE_RANK: Record<OrgRole, number> = { Viewer: 10, Developer: 20, Admin: 30, Owner: 40 };

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface User {
  id: string;
  email: string;
  displayName: string;
  createdAt: string;
}

export interface UserRef {
  id: string;
  displayName: string;
  email: string;
}

export interface OrgSummary {
  id: string;
  name: string;
  slug: string;
  role: OrgRole;
}

export interface Me {
  user: User;
  organizations: OrgSummary[];
  pendingInvitations: number;
}

export interface OrgDetail {
  id: string;
  name: string;
  slug: string;
  createdAt: string;
  auditRetentionDays: number;
  myRole: OrgRole;
  memberCount: number;
}

export interface Member {
  userId: string;
  email: string;
  displayName: string;
  role: OrgRole;
  joinedAt: string;
}

export interface Invitation {
  id: string;
  email: string;
  role: OrgRole;
  status: InvitationStatus;
  createdAt: string;
  expiresAt: string;
}

export interface InvitationCreated {
  invitation: Invitation;
  acceptToken: string;
  emailSent: boolean;
}

export interface InvitationPreview {
  id: string;
  organizationId: string;
  organizationName: string;
  email: string;
  role: OrgRole;
  status: InvitationStatus;
  expiresAt: string;
  emailMatchesCurrentUser: boolean;
}

export interface ProjectSummary {
  id: string;
  name: string;
  key: string;
  description: string | null;
  status: ProjectStatus;
  createdAt: string;
  updatedAt: string;
  openItems: number;
  totalItems: number;
  memberCount: number;
  myRole: ProjectRole | null;
}

export interface ProjectMember {
  userId: string;
  displayName: string;
  email: string;
  role: ProjectRole;
  orgRole: OrgRole;
  addedAt: string;
}

export interface Status {
  id: string;
  name: string;
  category: StatusCategory;
  position: number;
}

export interface Transition {
  fromStatusId: string;
  toStatusId: string;
}

export interface Label {
  id: string;
  name: string;
  color: string;
}

export interface ProjectPermissions {
  canEditItems: boolean;
  canManage: boolean;
  canDelete: boolean;
}

export interface ProjectDetail {
  id: string;
  name: string;
  key: string;
  description: string | null;
  status: ProjectStatus;
  createdAt: string;
  updatedAt: string;
  archivedAt: string | null;
  members: ProjectMember[];
  statuses: Status[];
  transitions: Transition[];
  labels: Label[];
  permissions: ProjectPermissions;
  openItems: number;
  totalItems: number;
}

export interface Workflow {
  statuses: Status[];
  transitions: Transition[];
}

export interface WorkItemSummary {
  id: string;
  projectId: string;
  key: string;
  number: number;
  type: WorkItemType;
  title: string;
  statusId: string;
  statusName: string;
  statusCategory: StatusCategory;
  priority: Priority;
  assignee: UserRef | null;
  dueDate: string | null;
  labels: Label[];
  parentId: string | null;
  subtaskCount: number;
  openSubtaskCount: number;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
}

export interface WorkItemRef {
  id: string;
  key: string;
  number: number;
  title: string;
  statusCategory: StatusCategory;
}

export interface WorkItemDetail {
  item: WorkItemSummary;
  description: string | null;
  reporter: UserRef | null;
  parent: WorkItemRef | null;
  subtasks: WorkItemSummary[];
  commentCount: number;
  version: number;
  allowedTransitions: Status[];
  permissions: ProjectPermissions;
}

export interface BoardColumn {
  status: Status;
  items: WorkItemSummary[];
  totalCount: number;
}

export interface Board {
  projectId: string;
  columns: BoardColumn[];
  transitions: Transition[];
  permissions: ProjectPermissions;
}

export interface Comment {
  id: string;
  author: UserRef | null;
  body: string;
  createdAt: string;
  editedAt: string | null;
  canEdit: boolean;
  canDelete: boolean;
}

export interface WorkItemChange {
  id: string;
  actor: UserRef | null;
  field: string;
  oldValue: string | null;
  newValue: string | null;
  at: string;
}

export interface Activity {
  id: string;
  projectId: string | null;
  actor: UserRef | null;
  kind: string;
  summary: string;
  subjectType: string;
  subjectId: string | null;
  at: string;
}

export interface Dashboard {
  work: {
    activeProjects: number;
    openItems: number;
    inProgressItems: number;
    completedLast30Days: number;
    openIssues: number;
    overdueItems: number;
    myOpenItems: number;
  };
  openByPriority: { priority: Priority; count: number }[];
  completionTrend: { weekStart: string; created: number; completed: number }[];
  recentActivity: Activity[];
  generatedAt: string;
}

export interface Notification {
  id: string;
  kind: string;
  title: string;
  body: string | null;
  link: string | null;
  createdAt: string;
  readAt: string | null;
}

export interface NotificationList {
  page: PagedResult<Notification>;
  unreadCount: number;
}

export interface WorkItemFilters {
  q?: string;
  statusId?: string;
  category?: StatusCategory;
  open?: boolean;
  assignee?: string;
  priority?: Priority;
  type?: WorkItemType;
  labelId?: string;
  overdue?: boolean;
  sort?: string;
  page?: number;
  pageSize?: number;
}
