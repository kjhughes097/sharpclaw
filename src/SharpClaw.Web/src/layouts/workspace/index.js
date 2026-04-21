import { useParams } from "react-router-dom";

import Grid from "@mui/material/Grid";

import MDBox from "components/MDBox";
import MDTypography from "components/MDTypography";
import WorkspaceProjectCard from "components/WorkspaceProjectCard";

import DashboardLayout from "examples/LayoutContainers/DashboardLayout";
import DashboardNavbar from "examples/Navbars/DashboardNavbar";
import Footer from "examples/Footer";

import useWorkspaceProjects from "hooks/useWorkspaceProjects";

function WorkspaceCategory() {
  const { category } = useParams();
  const { projects, loading } = useWorkspaceProjects(category);

  const displayName = category.charAt(0).toUpperCase() + category.slice(1);

  return (
    <DashboardLayout>
      <DashboardNavbar />
      <MDBox pt={6} pb={3}>
        <MDBox mb={3}>
          <MDTypography variant="h4" fontWeight="medium">
            {displayName}
          </MDTypography>
        </MDBox>
        {loading ? (
          <MDBox p={3} textAlign="center">
            <MDTypography variant="caption" color="text">
              Loading projects…
            </MDTypography>
          </MDBox>
        ) : projects.length === 0 ? (
          <MDBox p={3} textAlign="center">
            <MDTypography variant="body2" color="text">
              No projects yet. Add a project directory under {category}/ to get started.
            </MDTypography>
          </MDBox>
        ) : (
          <Grid container spacing={3}>
            {projects.map((project) => (
              <Grid item xs={12} sm={6} lg={4} key={project.slug}>
                <WorkspaceProjectCard {...project} />
              </Grid>
            ))}
          </Grid>
        )}
      </MDBox>
      <Footer />
    </DashboardLayout>
  );
}

export default WorkspaceCategory;
