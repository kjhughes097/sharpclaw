/* eslint-disable react/prop-types */
/* eslint-disable react/function-component-definition */

import { useState, useEffect } from "react";

// Material Dashboard 2 React components
import MDBox from "components/MDBox";
import MDTypography from "components/MDTypography";
import MDAvatar from "components/MDAvatar";
import MDBadge from "components/MDBadge";

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

const Agent = ({ name, slug }) => (
  <MDBox display="flex" alignItems="center" lineHeight={1}>
    <MDAvatar src={avatarMap[slug]} name={name} size="sm" />
    <MDBox ml={2} lineHeight={1}>
      <MDTypography display="block" variant="button" fontWeight="medium">
        {name}
      </MDTypography>
      <MDTypography variant="caption">{slug}</MDTypography>
    </MDBox>
  </MDBox>
);

const Role = ({ title }) => (
  <MDBox lineHeight={1} textAlign="left">
    <MDTypography display="block" variant="caption" color="text" fontWeight="medium">
      {title}
    </MDTypography>
  </MDBox>
);

const columns = [
  { Header: "agent", accessor: "agent", width: "25%", align: "left" },
  { Header: "description", accessor: "description", width: "35%", align: "left" },
  { Header: "model", accessor: "model", align: "left" },
  { Header: "tools", accessor: "tools", align: "left" },
  { Header: "status", accessor: "status", align: "center" },
];

function buildRow(agent) {
  return {
    agent: <Agent name={agent.name} slug={agent.slug} />,
    description: <Role title={agent.description} />,
    model: (
      <MDTypography variant="caption" color="text" fontWeight="medium">
        {agent.model}
      </MDTypography>
    ),
    tools: (
      <MDTypography variant="caption" color="text" fontWeight="medium">
        {(agent.tools || []).join(", ") || "—"}
      </MDTypography>
    ),
    status: (
      <MDBox ml={-1}>
        <MDBadge badgeContent="active" color="success" variant="gradient" size="sm" />
      </MDBox>
    ),
  };
}

export default function useAgentsTableData() {
  const [agents, setAgents] = useState([]);
  const [rows, setRows] = useState([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;

    fetch("/api/agents")
      .then((res) => res.json())
      .then((data) => {
        if (!cancelled) {
          setAgents(data);
          setRows(data.map(buildRow));
          setLoading(false);
        }
      })
      .catch(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, []);

  return { columns, rows, agents, loading };
}
