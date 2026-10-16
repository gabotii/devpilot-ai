import { Route, Routes } from 'react-router';
import { AcceptInvitationPage, OnboardingPage } from '../pages/OnboardingPages';
import { LoginPage, RegisterPage } from '../pages/AuthPages';
import { DashboardPage } from '../pages/DashboardPage';
import { ActivityPage, NotificationsPage, OrgSettingsPage, ProfilePage } from '../pages/OrgPages';
import { ProjectsPage } from '../pages/projects/ProjectsPage';
import { ProjectLayout } from '../pages/projects/ProjectLayout';
import { BoardView } from '../pages/projects/BoardView';
import { ProjectListView } from '../pages/projects/ProjectListView';
import { WorkItemPage } from '../pages/projects/WorkItemPage';
import { ProjectActivity, ProjectSettings } from '../pages/projects/ProjectSettings';
import { IncidentsPage } from '../pages/IncidentsPage';
import { OperationsRoutes } from '../pages/operations/OperationsRoutes';
import { AppShell } from './AppShell';
import { NotFoundPage } from './App';
import { HomeRedirect, OrgScope, RequireAuth } from './providers';

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/invite" element={<AcceptInvitationPage />} />
      <Route element={<RequireAuth />}>
        <Route path="/" element={<HomeRedirect />} />
        <Route path="/onboarding" element={<OnboardingPage />} />
        <Route path="/profile" element={<ProfilePage />} />
        <Route
          path="/o/:orgId"
          element={
            <OrgScope>
              <AppShell />
            </OrgScope>
          }
        >
          <Route index element={<DashboardPage />} />
          <Route path="projects" element={<ProjectsPage />} />
          <Route path="projects/:projectId" element={<ProjectLayout />}>
            <Route index element={<BoardView />} />
            <Route path="list" element={<ProjectListView />} />
            <Route path="activity" element={<ProjectActivity />} />
            <Route path="settings" element={<ProjectSettings />} />
            <Route path="items/:number" element={<WorkItemPage />} />
          </Route>
          <Route path="incidents/*" element={<IncidentsPage />} />
          <Route path="activity" element={<ActivityPage />} />
          <Route path="notifications" element={<NotificationsPage />} />
          <Route path="settings" element={<OrgSettingsPage />} />
          {OperationsRoutes()}
          <Route path="*" element={<NotFoundPage />} />
        </Route>
      </Route>
      <Route path="*" element={<NotFoundPage />} />
    </Routes>
  );
}
