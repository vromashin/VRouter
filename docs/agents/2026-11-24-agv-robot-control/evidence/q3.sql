\pset pager off
select "OrderId","Status","OrderUpdateId","TargetNodeId","CreatedAt","SentAt","AcceptedAt","CompletedAt","FailureReason"
from "vrt_RobotOrder" order by "CreatedAt" desc limit 4;
\echo '--- 185 nodes ---'
select "OrderId","SequenceId","NodeId","ActionCount","Reached","Released"
from "vrt_RobotOrderNode" where "OrderId"='ORD_00185' order by "SequenceId";
\echo '--- agv ---'
select "SerialNumber","HasLoad","CurrentOrderId","LastNodeId","PositionX","PositionY","Theta" from "Agvs";
\echo '--- tasks-events ---'
