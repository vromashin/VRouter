\pset pager off
\echo '--- ORD_00183 plan ---'
select "OrderId","Status","TargetNodeId","OrderUpdateId","SentAt","AcceptedAt","CompletedAt", left("PlanJson",400) as plan_head
from "vrt_RobotOrder" where "OrderId" in ('ORD_00183','ORD_00184');
\echo '--- 183 nodes ---'
select "OrderId","SequenceId","NodeId","ActionCount","Reached","Released"
from "vrt_RobotOrderNode" where "OrderId"='ORD_00183' order by "SequenceId";
\echo '--- node types ---'
\d+ "vrt_GraphNode"
