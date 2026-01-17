select * from [dbo].[ConfigConcepts]
--rate_limits	Rate Limiting	Global + policy rate limiting knobs.

------------------------------

select * from [dbo].[ConfigEntries]

-- global
--{
--  "perIdentityPerMinute": 300,
--  "burstPer10Seconds": 50
--}



-- enterprise
--{
--  "exports": {
--    "perTenantPerMinute": 600,
--    "perClientPerMinute": 300,
--    "perUserPerMinute": 120
--  },
--  "search": {
--    "perTenantPerMinute": 900,
--    "perClientPerMinute": 600,
--    "perUserPerMinute": 240
--  },
--  "login": {
--    "perIpPerMinute": 30,
--    "perClientPerMinute": 60
--  }
--}

select * from [dbo].[RateLimitIdentity]
select * from [dbo].[RateLimitMinuteAgg]
select * from [dbo].[RateLimitViolation]
select * from [dbo].[RateLimitViolationRows]

