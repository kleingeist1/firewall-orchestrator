# installation options for testing purposes

always change into the firewwall-orchestrator directory before starting the installation!

## testkeys - not for production use

Use the testkeys switch to always use the same fixed secrets.
This includes:
- jwt generation keys
- API hasura admin secret
- UI admin user
Note: the relevant secrets are displayed at the very end of the installation. They can also be found in the etc/secrets directory.

```console
ansible-playbook/ site.yml -e "testkeys=yes" -K
```

A static jwt key helps with debugging c# code in visual studio (code) - you can use a static backend (ldap & api) with these keys.

You need to
- add the config file and keys once on your local development machine
- set up an ssh tunnel to the back end machine

        sudo ssh -i /home/tim/.ssh/id_rsa -p 10222 tim@localhost -L 9443:localhost:9443 -L 636:localhost:636

    or to the central test server

        sudo ssh -i /home/tim/.ssh/id_rsa -p 60333 developer@cactus.de -L 9443:localhost:9443 -L 636:localhost:636

## Debugging

Set debug level for extended debugging info during installation.

```console
ansible-playbook/ site.yml -e "debug_level='2'" -K
```
## Running tests after installation

To only run tests (for an existing installation) use tags as follows:

```console
ansible-playbook/ site.yml --tags test -K
```

## Parameter "api_no_metadata" to prevent meta data import

e.g. if your hasura metadata file needs to be re-created from scratch, then use the following switch::

```console
ansible-playbook -e "api_no_metadata=yes" site.yml -K
```

## Parameter "without_sample_data" to not create sample data (i.e. in production)

The following command prevents the creation of sample data in the database:

```console
ansible-playbook -e "without_sample_data=yes" site.yml -K
```

note: demo/sample data can also be removed via settings menues.

## Parameter "test_ldap_external_ad_add_connection" to add test AD server

```console
ansible-playbook -e "test_ldap_external_ad_add_connection=yes" site.yml -K
```

### Parameter "second_ldap_db" to install second ldap database

if you want to install a second ldap database "dc=example,dc=com"

```console
cd firewall-orchestrator; ansible-playbook -e "second_ldap_db=yes" site.yml -K
```

## Parameter "connect_sting" to add Cactus test firewall CP R8x

The following command adds the sting test firewall to your fw orch system (needs VPN tunnel to Cactus)

```console
ansible-playbook -e "connect_sting=yes" site.yml -K
```

### Parameter "sample_data_rate" to ramp up sample data

if you want to create sample-data changes every minute set sample_data_rate to high

```console
cd firewall-orchestrator; ansible-playbook -e "sample_data_rate=high" site.yml -K
```
### Parameter "audit_user" to add an audit user to ldap db - useful for demo installation

if you want to have an extra read-only audit-user called e.g. auditor1, use the following command for installation:

```console
cd firewall-orchestrator; ansible-playbook -e "audit_user=auditor1 auditor_initial_pwd=<pwd>" site.yml -K
```
