/* eslint-disable react/prop-types */

import { useState, useEffect } from "react";

// @mui material components
import Card from "@mui/material/Card";
import Divider from "@mui/material/Divider";
import Grid from "@mui/material/Grid";
import Icon from "@mui/material/Icon";
import Chip from "@mui/material/Chip";

// Markdown editor
import MDEditor from "@uiw/react-md-editor";

// Material Dashboard 2 React components
import MDBox from "components/MDBox";
import MDTypography from "components/MDTypography";
import MDAvatar from "components/MDAvatar";

// Agent avatar images
import adeImg from "assets/images/agents/ade-head.png";
import codyImg from "assets/images/agents/cody-head.png";
import debbieImg from "assets/images/agents/debbie-head.png";
import noahImg from "assets/images/agents/noah-head.png";
import remyImg from "assets/images/agents/remy-head.png";
import routerImg from "assets/images/agents/router-head.png";

const avatarMap = {
  ade: adeImg,
  cody: codyImg,
  debbie: debbieImg,
  noah: noahImg,
  remy: remyImg,
  router: routerImg,
};

function AgentDetail({ agent }) {
  const [systemPrompt, setSystemPrompt] = useState(agent?.systemPrompt || "");

  useEffect(() => {
    setSystemPrompt(agent?.systemPrompt || "");
  }, [agent]);

  if (!agent) return null;

  const infoItems = [
    { label: "slug", value: agent.slug },
    { label: "service", value: agent.service },
    { label: "model", value: agent.model },
  ];

  return (
    <Card
      sx={{
        position: "relative",
        mt: -4,
        mx: 3,
        py: 2,
        px: 2,
      }}
    >
      <Grid container spacing={3} alignItems="center">
        <Grid item>
          <MDAvatar src={avatarMap[agent.slug]} alt={agent.name} size="xl" shadow="sm" />
        </Grid>
        <Grid item>
          <MDBox height="100%" mt={0.5} lineHeight={1}>
            <MDTypography variant="h5" fontWeight="medium">
              {agent.name}
            </MDTypography>
            <MDTypography variant="button" color="text" fontWeight="regular">
              {agent.description}
            </MDTypography>
          </MDBox>
        </Grid>
      </Grid>

      <MDBox mt={3} mb={1}>
        <Grid container spacing={2}>
          <Grid item xs={12} md={6}>
            <MDBox p={2}>
              <MDTypography variant="h6" fontWeight="medium" textTransform="capitalize">
                Agent Information
              </MDTypography>
              <MDBox opacity={0.3} mt={1}>
                <Divider />
              </MDBox>
              <MDBox>
                {infoItems.map(({ label, value }) => (
                  <MDBox key={label} display="flex" py={1} pr={2}>
                    <MDTypography variant="button" fontWeight="bold" textTransform="capitalize">
                      {label}: &nbsp;
                    </MDTypography>
                    <MDTypography variant="button" fontWeight="regular" color="text">
                      &nbsp;{value || "—"}
                    </MDTypography>
                  </MDBox>
                ))}
              </MDBox>
            </MDBox>
            <MDBox p={2}>
              <MDTypography variant="h6" fontWeight="medium" textTransform="capitalize">
                Tools
              </MDTypography>
              <MDBox opacity={0.3} mt={1}>
                <Divider />
              </MDBox>
              <MDBox display="flex" flexWrap="wrap" gap={1} mt={1}>
                {(agent.tools || []).length > 0 ? (
                  agent.tools.map((tool) => (
                    <Chip
                      key={tool}
                      label={tool}
                      size="small"
                      icon={<Icon fontSize="small">build</Icon>}
                    />
                  ))
                ) : (
                  <MDTypography variant="button" fontWeight="regular" color="text">
                    No tools configured
                  </MDTypography>
                )}
              </MDBox>
            </MDBox>
          </Grid>
          {agent.systemPrompt && (
            <Grid item xs={12} md={6}>
              <MDBox p={2} data-color-mode="light">
                <MDTypography variant="h6" fontWeight="medium" textTransform="capitalize">
                  System Prompt
                </MDTypography>
                <MDBox opacity={0.3} mt={1}>
                  <Divider />
                </MDBox>
                <MDBox mt={1}>
                  <MDEditor
                    value={systemPrompt}
                    onChange={(val) => setSystemPrompt(val || "")}
                    height={384}
                    preview="edit"
                  />
                </MDBox>
              </MDBox>
            </Grid>
          )}
        </Grid>
      </MDBox>
    </Card>
  );
}

export default AgentDetail;
