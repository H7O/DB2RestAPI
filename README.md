# Automatic Database-to-REST API converter

A no-code solution that automatically converts your SQL queries to RESTful APIs without API coding knowledge.

All you need to have is knowledge in writing basic SQL queries and you'll hopefully be on your way building safe and secure REST APIs in minutes.

And although not required, but if you happen to be a .NET developer, you can expand on this solution to add more features / customizations of your liking.

However, for public APIs or B2B APIs with API keys, you can use this solution as is. You'll be surprised how easy it is to build a REST API from a database using the DB-First approach offered by this solution.

## How to use

1. Create a sample database and name it `test`, then run the SQL script below to create a sample `phonebook` table within the `test` database that you just created.

> **Note**: Download and install either SQL Server Developer Edition or SQL Server Express if you don't have SQL Server installed on your machine

```sql
CREATE TABLE [dbo].[phonebook] (
    [id]    UNIQUEIDENTIFIER CONSTRAINT [DEFAULT_phonebook_id] DEFAULT (newid()) NOT NULL,
    [name]  NVARCHAR (500)   NULL,
    [phone] NVARCHAR (100)   NULL,
    CONSTRAINT [PK_phonebook] PRIMARY KEY CLUSTERED ([id] ASC)
);
```

2. Clone (or download) this repository.
3. Open the solution in Visual Studio.
4. Open the `/config/settings.xml` file and change the default `ConnectionStrings` to point to your `test` database.
5. Run the solution.
6. Download and install [Postman](https://www.postman.com/downloads/).
7. Open Postman and create a new request.
8. Set the request method to `POST` (or `GET`).
9. Set the request URL to `https://localhost:<your_custom_port>/hello_world`.
10. Fill `Content-Type` header with `application/json`.
11. Fill the request body with the following JSON:

```json
{
	"name": "John",
}
```
12. Send the request and you should see the following JSON respons: 
```json

[
    {
        "message_from_db": "hello John!"
    }
]
```
13. To see how the API works, change the `name` property in the request body to `Jane` and send the request again. You should see a different response from the database.
14. To see the SQL query that generated the response, open the `/config/sql.xml` file and look for the `hello_world` query. You can change the query to anything you want and the API will still work as long as the query is valid and returns at least a single row.
15. If you examine the `hello_world` query in `/config/sql.xml`, you'll find the use of the `{{name}}` parameter. This parameter is passed from the request body to the query. You can add as many parameters as you want and use them in your queries.
```sql
declare @name nvarchar(500) = {{name}};
        
if (@name is null or @name = '')
begin
    set @name = 'world';
end
select 'hello ' + @name + '!' as message_from_db;
```
> **Note**: Passing parameters is safe and secure. The solution is designed to protect against SQL injection attacks by default via utilizing SQL Server's built-in parameterization feature. 
> The SQL parameterization feature is offered by `Com.H.Data.Common` package (available on [Github](https://github.com/H7O/Com.H.Data.Common) / [Nuget](https://www.nuget.org/packages/Com.H.Data.Common/)).


## Phonebook API examples

### Example 1 - Adding a phonebook record

Now, let's try to create a new record in the `phonebook` table. 
1. To do that, change the request URL to `https://localhost:<your_custom_port>/add_contact` and change the request method to `POST`.
2. Fill `Content-Type` header with `application/json`.
3. Fill the request body with the following JSON: 
```json
{
	"name": "John",
	"phone": "1234567890"
}
```
4. Send the request and you should see the following JSON respons: 
```json
[
	{
		"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
		"name": "John",
		"phone": "1234567890"
	}
]
```
> **Note**: The `id` property is generated by the database and returned by the API. You can use this `id` to update or delete the record later.
> The `id` property is a `GUID` and is generated by the database by default. You can change the `id` property to be an `int` or `bigint` and set it to `IDENTITY` to be auto-incremented by the database.
> The `id` property in the sample response above is just a sample value and not the actual value that you'll get from the API.
5. To see how the API works, change the `name` and `phone` properties in the request body and send the request again. You should see a different response from the database.
6. Try adding multiple records with different names and phone numbers.
8. Try also adding the same name and phone number multiple times. 
You should get an error message from the database saying that the record already exists.
How this error is thrown from the database is up to you. 
The following XML tag in `/config/sql.xml` for `add_contact` illustrates how to throw an error from the database:
```xml
    <add_contact>
      <!--
      You can enforce mandatory parameters in your query by adding them in the `mandatory_parameters` node.
      Mandatory parameters are parameters that must be passed in the HTTP request.
      If any of the mandatory parameters are missing, the app will return an HTTP 400 error (bad request error).
      -->
      <mandatory_parameters>name,phone</mandatory_parameters>
      <query>
      <![CDATA[
        declare @name nvarchar(500) = {{name}};
        declare @phone nvarchar(100) = {{phone}};
      
        -- check if the contact already exists
      
        declare @existing_contact table 
        (
            id UNIQUEIDENTIFIER,
            name nvarchar(500),
            phone nvarchar(100)
        );
        insert into @existing_contact select top 1 id, name, phone from [phonebook] where name = @name and phone = @phone;
      
        declare @error_msg nvarchar(500);
      
        -- return an http 409 error (conflict error) if the contact already exists
      
        if ((select count(*) from [phonebook] where name = @name and phone = @phone) > 0)
        begin 
            set @error_msg = 'Contact with name ' + @name + ' and phone ' + @phone + ' already exists';
            -- to return http error code `409 Conflict` throw 50409 and the app will return 409.
            -- same for other http error codes, e.g. 404, 500, etc. Just throw 50404, 50500, etc.
            throw 50409, @error_msg, 1;
            return;
        end
      
      -- insert new contact, and return it back to the http client
      insert into [phonebook] (id, name, phone) 
      output inserted.id, inserted.name, inserted.phone
      values (newid(), @name, @phone)
      

    ]]>
      </query>
      
    </add_contact>
```
Notice how throwing any error with code number between 50000 and 51000 will be caught by the app and returned to the client as an HTTP error code between 0 and 1000.

For example, throwing error code 50409 will be returned to the client as HTTP error code 409 along with the message that you passed to the `throw` statement.

So basically, the reserved error code range 50000-51000 is mapped to the HTTP error code range 0-1000.

Anything outside the reserved error code range 50000-51000 will be returned to the client as HTTP error code 500 with a generic error message of `An error occurred while processing your request.`.

This is a safety measure to prevent exposing any sensitive information from the database to the client.

The default error message content can be changed in the `/config/settings.xml` file by changing the `default_generic_error_message` node.

### Example 2 - Updating a phonebook record

Now, let's try to update a record in the `phonebook` table.
1. To do that, change the request URL to `https://localhost:<your_custom_port>/update_contact` and change the request method to `POST`.
2. Fill `Content-Type` header with `application/json`.
3. Fill the request body with the following JSON: 
```json
{
	"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
	"name": "John Update 1",
	"phone": "1234567890"
}
```
4. Send the request and you should see the following JSON respons: 
```json
[
	{
		"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
		"name": "John Update 1",
		"phone": "1234567890"
	}
]
```
The response above shows the updated record.

5. To see how the API works, change the `name` and/or `phone` properties in the request body and send the request again. You should see a different response from the database.
6. Try updating the same record multiple times.
7. Try also updating a record that doesn't exist.
You should get an error message from the database saying that the record doesn't exist.
The error will be returned to the client as HTTP error code 404 (not found error).

### Example 3 - Retrieving phonebook records

Now, let's try to retrieve records from the `phonebook` table.
1. To do that, change the request URL to `https://localhost:<your_custom_port>/get_contacts` and change the request method to `POST`.
2. Fill `Content-Type` header with `application/json`.
3. Fill the request body with the following JSON: 
```json
{
	"name": "j"
}
```
The `name` property is a search parameter. The API will return all records that contain the `name` value in the `name` column. The search is case-insensitive.

You can also use the `phone` property as a search parameter. The API will return all records that contain the `phone` value in the `phone` column. The search is case-insensitive.

You can also use both `name` and `phone` properties as search parameters. The API will return all records that contain the `name` value in the `name` column and the `phone` value in the `phone` column. The search is case-insensitive.

Check the `/config/sql.xml` file for the `get_contact` query to see how the search parameters are used in the query.

4. Try passing `take` and `skip` parameters in the request body to limit the number of records returned and to skip a number of records.
```json
{
	"name": "j",
	"take": 10,
	"skip": 0
}
```

This helps in implementing pagination in your API. Check the `/config/sql.xml` file for the `get_contacts` query to see how the `take` and `skip` parameters are used in the query.

Also, to implement predictable pagination, check out the next example that demonstrates how to retrive records along with the total number of the returned records.

### Example 4 - Retrieving phonebook records along with the total number of records

Now, let's try to retrieve records from the `phonebook` table along with the total number of records.
1. To do that, change the request URL to `https://localhost:<your_custom_port>/get_contacts_with_count` and change the request method to `POST`.
1. Fill `Content-Type` header with `application/json`.
1. Fill the request body with the following JSON: 
```json
{
	"name": "j",
	"take": 3,
	"skip": 0
}
```
The `name` property is a search parameter. The API will return all records that contain the `name` value in the `name` column. The search is case-insensitive.

The difference this time compared to the previous example is that the API will return the total number of records that match the search criteria along with the records.

The total number of records is returned in the `count` property in the response.

The records are returned in the `data` property in the response.

```json
{
	"count": 20,
	"data": [
		{
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
			"name": "John Update 1",
			"phone": "1234567890"
		},
        {
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b1",
			"name": "John Update 2",
			"phone": "1234567890"
		},
		{
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b2",
			"name": "John Update 3",
			"phone": "1234567890"
		}
	]
}
```

The above response shows the first 3 records that match the search criteria along with the total number of records that match the search criteria.

To paginate through the records, you can change the `skip` parameter in the request body to skip a number of records.

Check the `/config/sql.xml` file for the `get_contact_with_count` node to see how the search parameters are used in the query.

```xml
    <get_contacts_with_count>
      <query>
        <![CDATA[
        declare @name nvarchar(500) = {{name}};
        declare @phone nvarchar(100) = {{phone}};
        declare @take int = {{take}};
        declare @skip int = {{skip}};
        -- default take to 100 if not specified
        if (@take is null or @take < 1)
        begin
            set @take = 100;
        end
        -- make sure max take doesn't exceed 1000
        if (@take > 1000)
        begin
            set @take = 1000;
        end
        -- default skip to 0 if not specified
        if (@skip is null or @skip < 0)
        begin
            set @skip = 0;
        end
        
      select * from [phonebook] 
        where 
          (@name is null or [name] like '%' +  @name + '%')
          or (@phone is null or [phone] like '%' +  @phone + '%')
        order by [name]
        offset @skip rows
        fetch next @take rows only;        
        
        ]]>
      </query>
      <count_query>
        <![CDATA[
        declare @name nvarchar(500) = {{name}};
        declare @phone nvarchar(100) = {{phone}};
        select count(*) from [phonebook] 
        where 
          (@name is null or [name] like '%' +  @name + '%')
          or (@phone is null or [phone] like '%' +  @phone + '%');
        
        ]]>
      </count_query>

    </get_contacts_with_count>
```

Notice how in the above query, the `take` and `skip` parameters are used to implement pagination.

Also, notice how the `count_query` node is used to return the total number of records that match the search criteria whereas the `query` node is used to return the actual records.


### Example 5 - Deleting a phonebook record

Now, let's try to delete a record in the `phonebook` table.
1. To do that, change the request URL to `https://localhost:<your_custom_port>/delete_contact` and change the request method to `POST`.
2. Fill `Content-Type` header with `application/json`.
3. Fill the request body with the following JSON: 
```json
{
	"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0"
}
```
> **Note**: The above is an example `id` value. You can use any `id` value that you get from the API when you add a new record or retrieve records.

4. Send the request and you should see the following JSON respons: 
```json
[
	{
		"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
		"name": "John Update 1",
		"phone": "1234567890"
	}
]
```
> **Note**: The above is an example response. You'll get the same response when you update a record but with different values. The response is just to show you the record that was deleted.

If the record doesn't exist, you'll get an error message from the database saying that the record doesn't exist.
The error will be returned to the client as HTTP error code 404 (not found error).


### Example 5 - Acting as an API gateway

The solution offers an option to act as an API gateway routing requests to different other APIs.

This is helpful as it offers a collection of APIs from a single unified base URL to consumers without 
those consumers have to be concerned about the technical details from where the data of those APIs are coming from, 
whether from calls to databases to which the solution has direct access, or from external APIs routed through this solution.

Also, in such a scenario, the solution can be used to enforce the same API keys for all APIs, whether local or external regardless
of whether or not the external APIs require API keys.

To achieve this, the solution has a setup file under `config` folder called `api_gateway.xml` that can be used to route requests to different APIs.

The `api_gateway.xml` file has the following structure:

```xml
<setting>
	<routes>
        <cat_facts>
            <url>https://catfact.ninja/fact</url>
            <headers_to_exclude_from_routing>x-api-key,host</headers_to_exclude_from_routing>
        </cat_facts>
        <hello_world_routed>
			<url>https://localhost:7054/hello_world</url>
			<headers_to_exclude_from_routing>x-api-key,host</headers_to_exclude_from_routing>
            <ignore_certificate_errors>true</ignore_certificate_errors>
        </hello_world_routed>
        <!-- 
        adds API Keys protection to to the unprotected `catfact.ninja/fact` API
        before routing
        -->
        <locally_protected_cat_facts>
          <api_keys>
            <key>local api key 1</key>
            <key>local api key 2</key>
          </api_keys>

          <url>https://catfact.ninja/fact</url>
          <headers_to_exclude_from_routing>x-api-key,host</headers_to_exclude_from_routing>

        </locally_protected_cat_facts>

        <!-- 
        calls the already protected `protected_hello_world` API and passes the `x-api-key`
        coming from the client request back to the API without doing 
        any API keys protection of its own upfront and relying
        on the protection of the `protected_hello_world` API
        -->
        <remote_protected_hello_world_routed>
          <url>https://localhost:7054/protected_hello_world</url>
          <headers_to_exclude_from_routing>host</headers_to_exclude_from_routing>
          <ignore_certificate_errors>true</ignore_certificate_errors>
        </remote_protected_hello_world_routed>


	</routes>
</settings>
```

The `routes` node contains a collection of routes that you can define.

Let's take the `cat_facts` route as an example:

```xml
<cat_facts>
	<url>https://catfact.ninja/fact</url>
	<headers_to_exclude_from_routing>x-api-key,host</headers_to_exclude_from_routing>
</cat_facts>
```

The `cat_facts` node is the name of the route. You can name it anything you want.

The `url` node is the URL to which the request will be routed.

The `headers_to_exclude_from_routing` node is an optional node having a comma-separated list of headers that you want to 
exclude from the request before routing it to the external API.

In the above example, the request will be routed to `https://catfact.ninja/fact` and the `x-api-key` and `host` headers will be excluded from the request before routing it to the external API.

The same applies to the `hello_world_routed` and `protected_hello_world_routed` routes.

To use the API gateway feature, you need to change the request URL to `https://localhost:<your_custom_port>/cat_facts` or `https://localhost:<your_custom_port>/hello_world_routed` or `https://localhost:<your_custom_port>/protected_hello_world_routed` and send the request.

The solution will route the request to the external API or the local API based on the route you specified in the `api_gateway.xml` file.

The solution will also exclude the headers you specified in the `api_gateway.xml` file from the request before routing it to the external API or the local API.

The reason for offering the option to exclude headers from the request before routing it to the external API is 
to prevent exposing sensitive information to the external API and remove
unwanted headers that might cause issues when routing the request to the external API.

For example, you must `host` header in the request before routing it to the external API to prevent causing TLS handshake errors when routing the request to the external API.

And also you might want to exclude the `x-api-key` header from the request before routing it to the external API to prevent exposing the API key of your solution to the external API.

`ignore_certificate_errors` node is an optional node that you can use to ignore certificate errors when routing the request to the external API.

This is useful when the external API has an invalid SSL certificate (e.g., self signed) and you want to ignore the certificate errors and route the request to the external API anyway.


**documentation in progress - more examples to be added soon**