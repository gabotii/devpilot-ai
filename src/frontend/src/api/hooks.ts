import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, qs } from './client';
import type {
  Activity,
  Board,
  Comment,
  Dashboard,
  Invitation,
  InvitationCreated,
  InvitationPreview,
  Label,
  Me,
  Member,
  NotificationList,
  OrgDetail,
  OrgRole,
  OrgSummary,
  PagedResult,
  Priority,
  ProjectDetail,
  ProjectRole,
  ProjectStatus,
  ProjectSummary,
  StatusCategory,
  Transition,
  User,
  WorkItemChange,
  WorkItemDetail,
  WorkItemFilters,
  WorkItemSummary,
  WorkItemType,
  Workflow,
} from './types';

export const keys = {
  me: ['me'] as const,
  myInvitations: ['invitations', 'mine'] as const,
  org: (orgId: string) => ['org', orgId] as const,
  members: (orgId: string, q?: string) => ['org', orgId, 'members', q ?? ''] as const,
  invitations: (orgId: string) => ['org', orgId, 'invitations'] as const,
  dashboard: (orgId: string) => ['org', orgId, 'dashboard'] as const,
  activity: (orgId: string, projectId?: string, page = 1) => ['org', orgId, 'activity', projectId ?? 'all', page] as const,
  projects: (orgId: string) => ['org', orgId, 'projects'] as const,
  projectList: (orgId: string, params: object) => ['org', orgId, 'projects', 'list', params] as const,
  project: (orgId: string, projectId: string) => ['org', orgId, 'projects', projectId] as const,
  board: (orgId: string, projectId: string, params: object) => ['org', orgId, 'projects', projectId, 'board', params] as const,
  items: (orgId: string, projectId: string | undefined, params: object) =>
    ['org', orgId, 'items', projectId ?? 'all', params] as const,
  item: (orgId: string, projectId: string, number: number) => ['org', orgId, 'projects', projectId, 'item', number] as const,
  comments: (orgId: string, projectId: string, number: number) =>
    ['org', orgId, 'projects', projectId, 'item', number, 'comments'] as const,
  history: (orgId: string, projectId: string, number: number) =>
    ['org', orgId, 'projects', projectId, 'item', number, 'history'] as const,
  notifications: (orgId: string, unread: boolean, page: number) => ['org', orgId, 'notifications', unread, page] as const,
};

const orgPath = (orgId: string) => `/api/orgs/${orgId}`;
const projectPath = (orgId: string, projectId: string) => `${orgPath(orgId)}/projects/${projectId}`;

// ---------- Auth ----------

export function useMe() {
  return useQuery({ queryKey: keys.me, queryFn: ({ signal }) => api.get<Me>('/api/auth/me', signal), retry: false });
}

export function useLogin() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { email: string; password: string }) => api.post<{ user: User }>('/api/auth/login', body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.me }),
  });
}

export function useRegister() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { email: string; displayName: string; password: string }) =>
      api.post<{ user: User }>('/api/auth/register', body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.me }),
  });
}

export function useLogout() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post('/api/auth/logout'),
    onSettled: () => qc.clear(),
  });
}

export function useUpdateProfile() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { displayName: string }) => api.put<User>('/api/auth/me/profile', body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.me }),
  });
}

export function useChangePassword() {
  return useMutation({
    mutationFn: (body: { currentPassword: string; newPassword: string }) => api.post('/api/auth/me/password', body),
  });
}

// ---------- Invitations (invitee side) ----------

export function useMyInvitations() {
  return useQuery({
    queryKey: keys.myInvitations,
    queryFn: ({ signal }) => api.get<InvitationPreview[]>('/api/invitations/mine', signal),
  });
}

export function usePreviewInvitation(token: string | undefined) {
  return useQuery({
    queryKey: ['invitation-preview', token],
    enabled: !!token,
    retry: false,
    queryFn: () => api.post<InvitationPreview>('/api/invitations/preview', { token }),
  });
}

export function useAcceptInvitation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: { token: string } | { id: string }) =>
      'token' in input
        ? api.post<OrgSummary>('/api/invitations/accept', { token: input.token })
        : api.post<OrgSummary>(`/api/invitations/${input.id}/accept`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: keys.me });
      void qc.invalidateQueries({ queryKey: keys.myInvitations });
    },
  });
}

// ---------- Organizations ----------

export function useCreateOrganization() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { name: string }) => api.post<OrgSummary>('/api/orgs', body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.me }),
  });
}

export function useOrganization(orgId: string) {
  return useQuery({ queryKey: keys.org(orgId), queryFn: ({ signal }) => api.get<OrgDetail>(orgPath(orgId), signal) });
}

export function useUpdateOrganization(orgId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { name: string; auditRetentionDays: number }) => api.put<OrgDetail>(orgPath(orgId), body),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: keys.org(orgId) });
      void qc.invalidateQueries({ queryKey: keys.me });
    },
  });
}

export function useMembers(orgId: string, q?: string) {
  return useQuery({
    queryKey: keys.members(orgId, q),
    queryFn: ({ signal }) => api.get<PagedResult<Member>>(`${orgPath(orgId)}/members${qs({ q, pageSize: 100 })}`, signal),
  });
}

export function useChangeMemberRole(orgId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ userId, role }: { userId: string; role: OrgRole }) =>
      api.put<Member>(`${orgPath(orgId)}/members/${userId}/role`, { role }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['org', orgId, 'members'] }),
  });
}

export function useRemoveMember(orgId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (userId: string) => api.del(`${orgPath(orgId)}/members/${userId}`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['org', orgId, 'members'] });
      void qc.invalidateQueries({ queryKey: keys.me });
    },
  });
}

export function useInvitations(orgId: string, enabled: boolean) {
  return useQuery({
    queryKey: keys.invitations(orgId),
    enabled,
    queryFn: ({ signal }) => api.get<Invitation[]>(`${orgPath(orgId)}/invitations`, signal),
  });
}

export function useCreateInvitation(orgId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { email: string; role: OrgRole }) => api.post<InvitationCreated>(`${orgPath(orgId)}/invitations`, body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.invitations(orgId) }),
  });
}

export function useRevokeInvitation(orgId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.del(`${orgPath(orgId)}/invitations/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.invitations(orgId) }),
  });
}

// ---------- Dashboard & activity ----------

export function useDashboard(orgId: string) {
  return useQuery({
    queryKey: keys.dashboard(orgId),
    queryFn: ({ signal }) => api.get<Dashboard>(`${orgPath(orgId)}/dashboard`, signal),
    refetchInterval: 60_000,
  });
}

export function useActivity(orgId: string, projectId?: string, page = 1) {
  return useQuery({
    queryKey: keys.activity(orgId, projectId, page),
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) =>
      api.get<PagedResult<Activity>>(`${orgPath(orgId)}/activity${qs({ projectId, page, pageSize: 20 })}`, signal),
  });
}

// ---------- Projects ----------

export function useProjects(orgId: string, params: { q?: string; status?: ProjectStatus; sort?: string; page?: number }) {
  return useQuery({
    queryKey: keys.projectList(orgId, params),
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) =>
      api.get<PagedResult<ProjectSummary>>(`${orgPath(orgId)}/projects${qs({ ...params, pageSize: 24 })}`, signal),
  });
}

export function useProject(orgId: string, projectId: string) {
  return useQuery({
    queryKey: keys.project(orgId, projectId),
    queryFn: ({ signal }) => api.get<ProjectDetail>(projectPath(orgId, projectId), signal),
  });
}

function useInvalidateProject(orgId: string, projectId?: string) {
  const qc = useQueryClient();
  return () => {
    void qc.invalidateQueries({ queryKey: projectId ? keys.project(orgId, projectId) : keys.projects(orgId) });
    void qc.invalidateQueries({ queryKey: keys.projects(orgId), exact: false });
    void qc.invalidateQueries({ queryKey: keys.dashboard(orgId) });
  };
}

export function useCreateProject(orgId: string) {
  const invalidate = useInvalidateProject(orgId);
  return useMutation({
    mutationFn: (body: { name: string; key: string; description?: string }) =>
      api.post<ProjectDetail>(`${orgPath(orgId)}/projects`, body),
    onSuccess: invalidate,
  });
}

export function useUpdateProject(orgId: string, projectId: string) {
  const invalidate = useInvalidateProject(orgId, projectId);
  return useMutation({
    mutationFn: (body: { name: string; description?: string | null }) => api.put<ProjectDetail>(projectPath(orgId, projectId), body),
    onSuccess: invalidate,
  });
}

export function useProjectAction(orgId: string, projectId: string) {
  const invalidate = useInvalidateProject(orgId, projectId);
  return useMutation({
    mutationFn: async (action: 'archive' | 'restore' | 'delete') => {
      if (action === 'delete') await api.del(projectPath(orgId, projectId));
      else await api.post<ProjectDetail>(`${projectPath(orgId, projectId)}/${action}`);
    },
    onSuccess: invalidate,
  });
}

export function useProjectMemberMutations(orgId: string, projectId: string) {
  const invalidate = useInvalidateProject(orgId, projectId);
  const base = `${projectPath(orgId, projectId)}/members`;
  return {
    add: useMutation({
      mutationFn: (body: { userId: string; role: ProjectRole }) => api.post<ProjectDetail>(base, body),
      onSuccess: invalidate,
    }),
    change: useMutation({
      mutationFn: ({ userId, role }: { userId: string; role: ProjectRole }) => api.put<ProjectDetail>(`${base}/${userId}`, { role }),
      onSuccess: invalidate,
    }),
    remove: useMutation({
      mutationFn: (userId: string) => api.del<ProjectDetail>(`${base}/${userId}`),
      onSuccess: invalidate,
    }),
  };
}

export function useWorkflowMutations(orgId: string, projectId: string) {
  const invalidate = useInvalidateProject(orgId, projectId);
  const base = `${projectPath(orgId, projectId)}/workflow`;
  type StatusBody = { name: string; category: StatusCategory; position: number };
  return {
    addStatus: useMutation({ mutationFn: (body: StatusBody) => api.post<Workflow>(`${base}/statuses`, body), onSuccess: invalidate }),
    updateStatus: useMutation({
      mutationFn: ({ id, ...body }: StatusBody & { id: string }) => api.put<Workflow>(`${base}/statuses/${id}`, body),
      onSuccess: invalidate,
    }),
    deleteStatus: useMutation({ mutationFn: (id: string) => api.del<Workflow>(`${base}/statuses/${id}`), onSuccess: invalidate }),
    setTransitions: useMutation({
      mutationFn: (transitions: Transition[]) => api.put<Workflow>(`${base}/transitions`, { transitions }),
      onSuccess: invalidate,
    }),
  };
}

export function useLabelMutations(orgId: string, projectId: string) {
  const invalidate = useInvalidateProject(orgId, projectId);
  const base = `${projectPath(orgId, projectId)}/labels`;
  return {
    create: useMutation({ mutationFn: (body: { name: string; color: string }) => api.post<Label>(base, body), onSuccess: invalidate }),
    remove: useMutation({ mutationFn: (id: string) => api.del(`${base}/${id}`), onSuccess: invalidate }),
  };
}

// ---------- Work items ----------

export function useBoard(orgId: string, projectId: string, params: { q?: string; assignee?: string; type?: WorkItemType; priority?: Priority }) {
  return useQuery({
    queryKey: keys.board(orgId, projectId, params),
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => api.get<Board>(`${projectPath(orgId, projectId)}/board${qs(params)}`, signal),
  });
}

export function useWorkItems(orgId: string, projectId: string | undefined, filters: WorkItemFilters) {
  const path = projectId ? `${projectPath(orgId, projectId)}/items` : `${orgPath(orgId)}/work-items`;
  return useQuery({
    queryKey: keys.items(orgId, projectId, filters),
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => api.get<PagedResult<WorkItemSummary>>(`${path}${qs({ ...filters })}`, signal),
  });
}

export function useWorkItem(orgId: string, projectId: string, number: number) {
  return useQuery({
    queryKey: keys.item(orgId, projectId, number),
    queryFn: ({ signal }) => api.get<WorkItemDetail>(`${projectPath(orgId, projectId)}/items/${number}`, signal),
  });
}

export interface CreateWorkItemBody {
  type: WorkItemType;
  title: string;
  description?: string;
  priority: Priority;
  assigneeId?: string | null;
  dueDate?: string | null;
  parentId?: string | null;
  statusId?: string | null;
  labelIds?: string[];
}

export interface UpdateWorkItemBody {
  title: string;
  description: string | null;
  priority: Priority;
  assigneeId: string | null;
  dueDate: string | null;
  labelIds: string[];
  version: number;
}

/** Invalidates every cached view of work items in an org (boards, lists, details, dashboard). */
export function useInvalidateWork(orgId: string) {
  const qc = useQueryClient();
  return () => {
    void qc.invalidateQueries({ queryKey: ['org', orgId] });
  };
}

export function useCreateWorkItem(orgId: string, projectId: string) {
  const invalidate = useInvalidateWork(orgId);
  return useMutation({
    mutationFn: (body: CreateWorkItemBody) => api.post<WorkItemDetail>(`${projectPath(orgId, projectId)}/items`, body),
    onSuccess: invalidate,
  });
}

export function useUpdateWorkItem(orgId: string, projectId: string, number: number) {
  const invalidate = useInvalidateWork(orgId);
  return useMutation({
    mutationFn: (body: UpdateWorkItemBody) => api.put<WorkItemDetail>(`${projectPath(orgId, projectId)}/items/${number}`, body),
    onSuccess: invalidate,
  });
}

export function useTransitionWorkItem(orgId: string, projectId: string) {
  const invalidate = useInvalidateWork(orgId);
  return useMutation({
    mutationFn: ({ number, statusId }: { number: number; statusId: string }) =>
      api.post<WorkItemDetail>(`${projectPath(orgId, projectId)}/items/${number}/transition`, { statusId }),
    onSettled: invalidate,
  });
}

export function useDeleteWorkItem(orgId: string, projectId: string) {
  const invalidate = useInvalidateWork(orgId);
  return useMutation({
    mutationFn: (number: number) => api.del(`${projectPath(orgId, projectId)}/items/${number}`),
    onSuccess: invalidate,
  });
}

export function useComments(orgId: string, projectId: string, number: number) {
  return useQuery({
    queryKey: keys.comments(orgId, projectId, number),
    queryFn: ({ signal }) =>
      api.get<PagedResult<Comment>>(`${projectPath(orgId, projectId)}/items/${number}/comments?pageSize=100`, signal),
  });
}

export function useCommentMutations(orgId: string, projectId: string, number: number) {
  const qc = useQueryClient();
  const base = `${projectPath(orgId, projectId)}/items/${number}/comments`;
  const invalidate = () => {
    void qc.invalidateQueries({ queryKey: keys.comments(orgId, projectId, number) });
    void qc.invalidateQueries({ queryKey: keys.item(orgId, projectId, number) });
  };
  return {
    add: useMutation({ mutationFn: (body: string) => api.post<Comment>(base, { body }), onSuccess: invalidate }),
    edit: useMutation({
      mutationFn: ({ id, body }: { id: string; body: string }) => api.put<Comment>(`${base}/${id}`, { body }),
      onSuccess: invalidate,
    }),
    remove: useMutation({ mutationFn: (id: string) => api.del(`${base}/${id}`), onSuccess: invalidate }),
  };
}

export function useHistory(orgId: string, projectId: string, number: number) {
  return useQuery({
    queryKey: keys.history(orgId, projectId, number),
    queryFn: ({ signal }) => api.get<WorkItemChange[]>(`${projectPath(orgId, projectId)}/items/${number}/history`, signal),
  });
}

// ---------- Notifications ----------

export function useNotifications(orgId: string, unread = false, page = 1, pageSize = 20) {
  return useQuery({
    queryKey: keys.notifications(orgId, unread, page),
    refetchInterval: 30_000,
    queryFn: ({ signal }) =>
      api.get<NotificationList>(`${orgPath(orgId)}/notifications${qs({ unread, page, pageSize })}`, signal),
  });
}

export function useNotificationMutations(orgId: string) {
  const qc = useQueryClient();
  const invalidate = () => qc.invalidateQueries({ queryKey: ['org', orgId, 'notifications'] });
  return {
    markRead: useMutation({ mutationFn: (id: string) => api.post(`${orgPath(orgId)}/notifications/${id}/read`), onSuccess: invalidate }),
    markAll: useMutation({ mutationFn: () => api.post(`${orgPath(orgId)}/notifications/read-all`), onSuccess: invalidate }),
  };
}
