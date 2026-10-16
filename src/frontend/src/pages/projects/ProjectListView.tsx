import { WorkItemTable } from './ListView';
import { useProjectDetail } from './ProjectLayout';

export function ProjectListView() {
  const project = useProjectDetail();
  return <WorkItemTable projectId={project.id} project={project} />;
}
