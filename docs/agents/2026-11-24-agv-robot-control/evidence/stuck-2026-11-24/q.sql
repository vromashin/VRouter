\pset pager off
select "OrderId","Status","OrderUpdateId","TargetNodeId","AgvSerialNumber","CreatedAt","SentAt","AcceptedAt","CompletedAt","FailureReason"
from "vrt_RobotOrder" order by "CreatedAt" desc limit 6;
select "OrderId","SequenceId","NodeId","ActionCount","Reached","Released"
from "vrt_RobotOrderNode"
where "OrderId" in (select "OrderId" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2)
order by "OrderId","SequenceId";
select "SerialNumber","HasLoad","CurrentOrderId","LastNodeId","PositionX","PositionY","Theta" from "Agvs";
