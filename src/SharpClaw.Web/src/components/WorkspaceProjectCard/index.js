/* eslint-disable react/prop-types */

import { Link } from "react-router-dom";

// @mui material components
import Card from "@mui/material/Card";
import CardMedia from "@mui/material/CardMedia";
import Chip from "@mui/material/Chip";
import Icon from "@mui/material/Icon";
import Tooltip from "@mui/material/Tooltip";

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

const statusColors = {
  live: "success",
  archived: "default",
  stale: "warning",
};

function formatDate(dateStr) {
  if (!dateStr) return "—";
  const d = new Date(dateStr);
  return d.toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" });
}

function formatTokens(count) {
  if (!count || count === 0) return "0";
  if (count >= 1000000) return `${(count / 1000000).toFixed(1)}M`;
  if (count >= 1000) return `${(count / 1000).toFixed(1)}k`;
  return count.toString();
}

function WorkspaceProjectCard({
  slug,
  name,
  category,
  status,
  createdAt,
  lastModifiedAt,
  totalTokens,
  icon,
  image,
  collaborators,
}) {
  const renderCollaborators = (collaborators || []).map((agentSlug) => (
    <Tooltip key={agentSlug} title={agentSlug} placement="bottom">
      <MDAvatar
        src={avatarMap[agentSlug]}
        alt={agentSlug}
        size="xs"
        sx={({ borders: { borderWidth }, palette: { white } }) => ({
          border: `${borderWidth[2]} solid ${white.main}`,
          cursor: "pointer",
          position: "relative",
          ml: -1.25,
          "&:hover, &:focus": { zIndex: "10" },
        })}
      />
    </Tooltip>
  ));

  return (
    <Card sx={{ height: "100%" }}>
      {image && (
        <MDBox position="relative" borderRadius="lg" mt={-3} mx={2}>
          <CardMedia
            src={image}
            component="img"
            title={name}
            sx={{
              maxWidth: "100%",
              borderRadius: "0.75rem",
              boxShadow: ({ boxShadows: { md } }) => md,
              objectFit: "cover",
              objectPosition: "center",
              height: "10rem",
            }}
          />
        </MDBox>
      )}
      <MDBox p={2} pt={image ? 2 : 3}>
        {/* Header row: icon + title + status */}
        <MDBox display="flex" alignItems="center" justifyContent="space-between" mb={1}>
          <MDBox display="flex" alignItems="center" gap={1}>
            {icon && (
              <Icon fontSize="medium" color="info">
                {icon}
              </Icon>
            )}
            <MDTypography
              component={Link}
              to={`/${category}/${slug}`}
              variant="h6"
              fontWeight="medium"
              sx={{ "&:hover": { textDecoration: "underline" } }}
            >
              {name}
            </MDTypography>
          </MDBox>
          <Chip
            label={status}
            size="small"
            color={statusColors[status] || "default"}
            sx={{ textTransform: "capitalize", fontWeight: 600 }}
          />
        </MDBox>

        {/* Dates */}
        <MDBox display="flex" gap={3} mb={1.5}>
          <MDBox>
            <MDTypography variant="caption" color="text" fontWeight="bold">
              Created
            </MDTypography>
            <MDTypography variant="caption" color="text" display="block">
              {formatDate(createdAt)}
            </MDTypography>
          </MDBox>
          <MDBox>
            <MDTypography variant="caption" color="text" fontWeight="bold">
              Modified
            </MDTypography>
            <MDTypography variant="caption" color="text" display="block">
              {formatDate(lastModifiedAt)}
            </MDTypography>
          </MDBox>
        </MDBox>

        {/* Footer: tokens + collaborators */}
        <MDBox display="flex" alignItems="center" justifyContent="space-between">
          <MDBox display="flex" alignItems="center" gap={0.5}>
            <Icon fontSize="small" color="action">
              token
            </Icon>
            <MDTypography variant="caption" color="text" fontWeight="medium">
              {formatTokens(totalTokens)} tokens
            </MDTypography>
          </MDBox>
          {collaborators && collaborators.length > 0 && (
            <MDBox display="flex" ml={1}>
              {renderCollaborators}
            </MDBox>
          )}
        </MDBox>
      </MDBox>
    </Card>
  );
}

export default WorkspaceProjectCard;
