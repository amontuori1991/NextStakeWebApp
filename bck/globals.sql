--
-- PostgreSQL database cluster dump
--

\restrict eQGh0CSd12s3AwHeKzcjKQKHk9gHtndPHtzcMjUPSVeZauPperdD9RTfqdYg3GK

SET default_transaction_read_only = off;

SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;

--
-- Roles
--

CREATE ROLE neondb_owner;
ALTER ROLE neondb_owner WITH SUPERUSER INHERIT CREATEROLE CREATEDB LOGIN REPLICATION BYPASSRLS PASSWORD 'SCRAM-SHA-256$4096:QDps+CQSa/BD7Vy4iJodgg==$fB3nZKBetug0YlI5JrGmvZgtdhZpqe+fs65n1tYR5qA=:VPfXkGTm19y09xWdxqIBHlF1NdZphxhVoKhwOoRiNU0=';

--
-- User Configurations
--








\unrestrict eQGh0CSd12s3AwHeKzcjKQKHk9gHtndPHtzcMjUPSVeZauPperdD9RTfqdYg3GK

--
-- PostgreSQL database cluster dump complete
--

