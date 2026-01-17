select * from [dbo].[ConfigConcepts]
--rate_limits	Rate Limiting	Global + policy rate limiting knobs.

------------------------------

select * from [dbo].[ConfigEntries]
--update [dbo].[ConfigEntries] set Value = '{"exports":{"perTenantPerMinute":6,"perClientPerMinute":3,"perUserPerMinute":1},"search":{"perTenantPerMinute":9,"perClientPerMinute":6,"perUserPerMinute":2},"login":{"perIpPerMinute":1,"perClientPerMinute":6}}' where id = 2

-- global
--{
--  "perIdentityPerMinute": 3,
--  "burstPer10Seconds": 50
--}



-- enterprise
--{
--  "exports": {
--    "perTenantPerMinute": 6,
--    "perClientPerMinute": 3,
--    "perUserPerMinute": 1
--  },
--  "search": {
--    "perTenantPerMinute": 9,
--    "perClientPerMinute": 6,
--    "perUserPerMinute": 2
--  },
--  "login": {
--    "perIpPerMinute": 1,
--    "perClientPerMinute": 60
--  }
--}

select * from [dbo].[RateLimitIdentity]
select * from [dbo].[RateLimitMinuteAgg]
select * from [dbo].[RateLimitViolation]
select * from [dbo].[RateLimitViolationRows]

