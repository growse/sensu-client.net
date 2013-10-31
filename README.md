sensu-client.net
================

An implementation of the sensu client in .NET for those that don't want to drag around a fully Ruby runtime on Windows. I'm not claiming this to be fully featured, but should support some task execution and simple checks.

Installation
============

The MSI will install a service called 'Sensu Client.net' and application into %PROGRAM FILES%. It provides a sample sensu-compatible json-based config file in the installation directory. Sensu-client.net will then log to %PROGRAMDATA%\sensu-client.net\logs.

Download
========

Current version 0.1.5:

https://s3-eu-west-1.amazonaws.com/sensuclientdotnet/sensu-client.net.0.1.5.msi
