import { Routes, Route } from 'react-router-dom';
import DashboardLayout from './layouts/DashboardLayout';
import HomePage from './pages/HomePage';
import AgentListPage from './pages/AgentListPage';
import AgentChatPage from './pages/AgentChatPage';
import AgentEditorPage from './pages/AgentEditorPage';
import McpListPage from './pages/McpListPage';
import McpEditorPage from './pages/McpEditorPage';
import ToolListPage from './pages/ToolListPage';
import ToolDetailPage from './pages/ToolDetailPage';
import SkillListPage from './pages/SkillListPage';
import SkillEditorPage from './pages/SkillEditorPage';
import TaskListPage from './pages/TaskListPage';
import TaskCreatePage from './pages/TaskCreatePage';
import TaskEditorPage from './pages/TaskEditorPage';
import ProjectsPage from './pages/ProjectsPage';
import ConfigPage from './pages/ConfigPage';
import ExamplesPage from './pages/ExamplesPage';

export default function App() {
    return (
        <Routes>
            <Route element={<DashboardLayout />}>
                <Route path="/" element={<HomePage />} />
                <Route path="/agents" element={<AgentListPage />} />
                <Route path="/agents/new" element={<AgentEditorPage />} />
                <Route path="/agents/:name" element={<AgentChatPage />} />
                <Route path="/agents/:name/edit" element={<AgentEditorPage />} />
                <Route path="/mcps" element={<McpListPage />} />
                <Route path="/mcps/new" element={<McpEditorPage />} />
                <Route path="/mcps/:name" element={<McpEditorPage />} />
                <Route path="/tools" element={<ToolListPage />} />
                <Route path="/tools/:name" element={<ToolDetailPage />} />
                <Route path="/skills" element={<SkillListPage />} />
                <Route path="/skills/new" element={<SkillEditorPage />} />
                <Route path="/skills/:name" element={<SkillEditorPage />} />
                <Route path="/tasks" element={<TaskListPage />} />
                <Route path="/tasks/new" element={<TaskCreatePage />} />
                <Route path="/tasks/:id" element={<TaskEditorPage />} />
                <Route path="/projects" element={<ProjectsPage />} />
                <Route path="/config" element={<ConfigPage />} />
                <Route path="/examples" element={<ExamplesPage />} />
            </Route>
        </Routes>
    );
}
