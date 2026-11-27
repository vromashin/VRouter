\pset pager off
select "OrderId","Status","OrderUpdateId","TargetNodeId","ApproachNodeId","CreatedAt","SentAt","AcceptedAt","CompletedAt"
from "vrt_RobotOrder" order by "CreatedAt" desc limit 8;
\echo '--- nodes of ORD_00184 ---'
select "OrderId","SequenceId","NodeId","ActionCount","Reached","Released"
from "vrt_RobotOrderNode" where "OrderId"='ORD_00184' order by "SequenceId";
\echo '--- agv ---'
select "SerialNumber","HasLoad","CurrentOrderId","LastNodeId","PositionX","PositionY","Theta","MapId" from "Agvs";
\echo '--- wo node types ---'
select "NodeId","NodeType","X","Y" from "WarehouseNodes" where "NodeId" in ('wo-2-7','wo-1-6','wa62','wa178') order by "NodeId";
