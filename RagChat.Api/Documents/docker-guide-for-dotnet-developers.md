# Docker Guide for .NET 10 Developers - Step-by-Step Tutorial

This comprehensive Docker Essentials for .NET Developers Guide will cover everything you need to know about Docker, from the basics to advanced concepts. Docker has become an indispensable tool in modern software development, streamlining workflows and boosting productivity, whether you are a .NET developer or work with another tech stack. Whether new to Docker or seeking to deepen your understanding, this guide will provide you with the knowledge and skills necessary to use Docker in your projects effectively. Learn how Docker can revolutionize your development process.

You'll begin by learning the basics of Docker, its architecture, and how it differs from a Virtual Machine. Next, you'll install Docker Desktop on your development machine, explore its features, and learn some essential Docker commands. Following that, you will containerize a simple .NET application, expand it with database services, and get introduced to Docker Compose.

> This guide will help you build and deploy distributed applications efficiently using Docker.

> **TL;DR.** Docker packages your .NET 10 application with all its dependencies into a portable container image that runs identically across dev, staging, and production. The recommended approach for .NET 10: write a **multi-stage Dockerfile** (SDK image for build, ASP.NET runtime image for run) to keep final images under 100 MB, use **`mcr.microsoft.com/dotnet/aspnet:10.0`** as the runtime base (or **chiseled images** for ~25 MB final size), define multi-container setups with **Docker Compose** (typically API + PostgreSQL), and run as a **non-root user** in production. .NET 7+ also supports **Dockerfile-less containerization** via the SDK (`dotnet publish /t:PublishContainer`) - useful when your build is uniform and you don't need custom layers. The mistake most teams make is using `mcr.microsoft.com/dotnet/sdk:10.0` as the runtime image - it's 5x larger than the runtime image and exposes the build toolchain in production.

Let's imagine that you are all set to deploy your shiny new .NET application to a different environment, whether for QA Testing or even to a customer-facing environment. You would probably expect things to go well since it was working without any issues on your development machine. However, this might not always be the case.

Once deployed, you might encounter several issues that you hadn't anticipated. Haven't you all faced this at some point in the careers?

> I guess we have all said this to a QA / Tester!

Anyhow, what was the root cause?

1. Environment Configuration Differences - Different environments (development, QA, production) may have different configurations, installed software, or operating system versions.
2. Dependency Management Issues - Different versions of libraries or dependencies might be installed in different environments, leading to compatibility issues.
3. Configuration Settings - Configuration settings such as environment variables, connection strings, and API keys might differ between environments.
4. Network Differences - Networking configurations, such as port mappings and network access, might vary between your local machine and the QA environment.
5. Resource Availability - Different environments may have varying amounts of CPU, memory, or disk space, affecting application performance.
6. Manual Application Specific Configuration Differences.
7. And lots of other surprises.

Thus, you need a way to run your applications consistently no matter what environment you put it on, from development to production.

## What's Docker? Why should you use Docker?

[Docker](https://www.docker.com/) is an open-source project that can pack, build, and run applications as containers which are both hardware-agnostic and platform-agnostic. This means that your application can run anywhere consistently. In simpler words, Docker is a standardized way to package your .NET (or any stack) application along with every bit and piece it needs to function, in any environment.

Docker essentially can package your application and all its related dependencies into Docker Images. These images can be pushed to a central repository, for example, Docker Hub or Amazon Elastic Container Registry, and can be run on any server! I'll explore more about this in a later section of this article.

It encapsulates all dependencies, configurations, and system libraries, eliminating discrepancies that often arise due to environmental differences. Docker's automated and reproducible setup processes reduce the risk of manual configuration errors and provide consistent networking, resource management, and security isolation. By adopting Docker, you can achieve a reliable and predictable deployment process, minimizing the "it works on my machine" problem and streamlining the path from development to production. And nowadays, you can simply NOT avoid Docker. It's a part of almost any scale of Application Build Pipelines.

Now that you understand why Docker matters, let's learn about the `HOW`.

## How does Docker Work?

To understand how Docker works, let's go through the building blocks of Docker.

1. Docker Images
2. Docker Containers
3. Dockerfile
4. Docker CLI
5. Docker Daemon
6. Docker Hub
7. Docker Compose
8. Docker Desktop
9. Docker Volumes
10. Docker Network

### Docker Images

A Docker image is a lightweight, standalone, executable package of software that includes everything needed to run an application: code, runtime, system tools, libraries, and settings. An image is essentially a snapshot of a container. It is built from a series of layers, each representing a step in the build process, and these layers are stacked on top of each other to form the final image.

- Base Image: The starting point of your Docker image, often including the operating system.
- Layers: Each command in a Dockerfile (the script to create an image) adds a new layer to the image. Layers are cached and reused, making builds efficient.

Docker images can be shared across different environments (development, testing, production) ensuring consistency. You can build your own Docker Images using the Dockerfile, or re-use an already available Image as well. More about this in the upcoming sections of this article.

### Docker Containers

A Docker container is a runtime instance of a Docker image. Containers are isolated environments that run on a single operating system kernel, sharing the same OS resources but keeping applications isolated from one another.

### Dockerfile

A Dockerfile is a file containing instructions to build a Docker Image. It defines the base image, application code, and other dependencies.

### Docker CLI

This is the command-line interface used to interact with Docker and manage images, containers, networks, and volumes. You will learn a few essential CLI commands in the next sections.

### Docker Daemon

This is the host process that manages Docker containers within your system. It listens to Docker API requests from the CLI client or Docker Desktop and handles Docker objects like images, containers, networks, and volumes.

### Docker Hub

So, now you know that you use Dockerfile to build Docker Images, which in turn can be executed as Docker Containers. Docker Hub is a central repository where you can store the Docker Images that you have built. This is a versioned repository, meaning you can store multiple versions of your Image, and pull them as required. Users can pull images from Docker Hub to create containers or push their images to share with others. Note that there are several alternatives to Docker Hub, like Amazon Elastic Container Registry (ECR), and GitHub Packages. I currently tend to use GitHub Packages for my open-source work as it well suits my use cases and is free to use.

### Docker Compose

A tool for defining and running multi-container Docker applications. It uses a YAML file to configure the application's services, networks, and volumes. This is an interesting one. You will be building a Docker Compose file that can run both a .NET Web API and a PostgreSQL instance (which the API connects to).

### Docker Desktop

Docker Desktop is an easy-to-install application for your Mac or Windows environment that enables you to build, share, and run containerized applications and microservices. It includes Docker Engine, Docker CLI, Docker Compose, and all essentials. If you are getting started with Docker, get this installed on your machine.

### Docker Volumes

Docker volumes are a critical part of Docker's data persistence strategy. They allow you to store data outside your container's file system, which ensures that data is not lost when a container is deleted or recreated.

### Docker Network

Docker networking is a vital component of Docker, allowing containers to communicate with each other, the host system, and external networks. Docker provides several networking options to meet different needs.

1. Bridge Network: Containers on the same bridge network can communicate with each other, but not with containers on different bridge networks.
2. Host Network: The container uses the host's networking directly.
3. Overlay Network: Allows containers running on different Docker hosts to communicate.
4. None Network: Containers are not attached to any network. Used for security reasons or when a container doesn't need networking.

This is a bit more complex topic, however, you must understand Docker supports various kinds of networking for its containers.

## Docker Workflow

Now that you have gone through all the basics, let's understand how these components work together to simplify your overall deployment experience.

1. The Developer writes a Dockerfile, which is a set of instructions to build a Docker Image.
2. As part of the CI/CD pipeline, this Dockerfile will be used to build the Docker Image via the Docker Daemon and CLI commands.
3. Once the image is built, it is pushed to a central repository (public/private), from where users can pull the image.
4. The client would have to run a pull command, to pull the newly pushed images into the environment where it needs to run.
5. Once pulled, you can spin up a Docker Container using this image.
6. If you need multiple Docker Containers (that can interact with each other), you would write a Docker Compose file.

## Docker Containers vs Virtual Machines.

But, doesn't Docker Container sound like a VM? It's correct that both of them give your applications an isolated environment. However, containers are much faster to boot up and are significantly less resource-intensive.

VMs are generally spun to run software applications. It is important to note that both Virtual Machines and Docker containers are resource virtualization technologies, however, the key difference is that VMs virtualize an entire machine including its hardware layer, but containers only virtualize the software layers above the operating system. You can also note that containers are an isolated process/service, whereas VMs are an entire virtual OS running on your machine.

Although VMs do provide complete isolation for applications, they come at a great computational cost due to all of their virtualization. Containers can achieve this at a fraction of the above-used compute power. However, there are solid use cases to go for a Virtual machine instead.

> I am currently building a Home Server that uses Virtualization to its fullest. As in, an Ubuntu Server is running as a VM over a hypervisor, and within the VM I have dozens of Docker Containers running. It's so cool to use such tech at your home lab!

Thus, Containers require significantly fewer resources as compared to the requirements to run a Virtual Machine.

But for the use case, where you need to debug, run, or deploy the .NET applications, a Docker container is the way to go!

## Installing Docker Desktop

Let's get into some action.

Docker Desktop is a GUI tool that helps manage Docker Containers and Images.

You can [download Docker Desktop](https://www.docker.com/products/docker-desktop/) for your operating system. Just follow the on-screen instructions and get it installed on your machine.

Make sure to create a free Docker Hub account as well.

## Exploring Docker Desktop

Once you have installed Docker Desktop, you will see something like the below.

- Containers: Displays a list of containers running on your machine.
- Images: Shows the available images on the machine.
- Volumes: This shows the shared storage space that you have created and assigned for your containers.

## Essential Docker CLI Commands

### **docker version**

To get the current version of your Docker instance.

```
docker -v
```

### **docker pull**

To pull an image from the docker hub repository. For instance, if I want to pull a copy of the latest Redis image, I should use the following.

```
docker pull redis:latest
```

Here, `redis` is the name of the image, and `latest` is the required version of the image. You can browse other [Docker Official Images](https://hub.docker.com/search?image_filter=official).

### **docker run**

The command to run a container using a docker image. To run an instance of Redis using the image you pulled in the earlier step, run the following command.

```
docker run --name redis -d -p 6379:6379 redis
```

Here, you instruct Docker to run a new container named `redis`, and specify its port mapping from 6379 internal port to 6379 at the host network. You also have to pass the name of the image you intend to use.

`-d` flag stands for detached mode, meaning the container runs in the background. If you don't use the `-d` flag, the container runs in the foreground. The terminal will display the output from the container, such as log messages and error outputs. You will need to keep the terminal session open for the container to keep running. You can stop the container by pressing `Ctrl+C`.

However, if you use the `-d` flag like you did here, the container runs in the background, and you immediately get your terminal prompt back. To view the logs, you can use `docker logs <container-name>` or `docker logs <container-id>`.

### **docker ps**

This lists all the available containers.

```
docker ps
```

### **docker images**

This lists down all the available images on your machine.

```
docker images
```

### **docker exec**

If you want to run a command within the container, use the exec terminal command. For example, since you already have the Redis container up and running, to execute some commands within the Redis container, you would do the following.

```
docker exec -it redis sh
```

And here are some Redis CLI commands that I ran against the Docker Container.

### **docker stop**

Stops a specific container.

```
docker stop redis
```

### **docker restart**

Restarts a specific container.

```
docker restart redis
```

These are the essential CLI commands regarding Docker. However, you missed a crucial one, which is the build command!

In the next section, you will build a sample .NET 10 Application, write a Dockerfile for it, containerize it, build it, and push it to Docker Hub. Let's get started!

### **docker prune**

Once in a while, I use this command to free up some space by removing unused Docker Images, Containers, Caches, and networks if any. This constantly saves me about 1-2 GB space depending on my usage history.

```
docker system prune
```

## Containerizing .NET Application

From .NET 7 and beyond, Containerizing a .NET application has become a lot simpler. Remember when I told about Dockerfile, and how it contains instructions to build and docker image? Well, from .NET 7 you no longer need a Dockerfile. The .NET SDK is intelligent enough to build a Docker image by just passing a couple of metadata arguments. However, it is still important to learn how Dockerfile functions, since it will be quite handy for certain advanced use cases.

> To read about the Built-In Container Support for .NET starting from .NET 7 and matured in .NET 10, see the dedicated guide on [Containerizing .NET 10 Apps Without a Dockerfile](/blog/containerize-dotnet-without-dockerfile/) - it covers `dotnet publish /t:PublishContainer`, chiseled images, multi-arch builds, and when SDK-based containerization beats writing a Dockerfile.

I would still recommend you go through the Dockerfile approach, as it gives you more hands-on experience and lets you understand how everything works. But before that, let's set up a .NET 10 sample project.

It would be a simple .NET 10 API with a single endpoint.

```csharp
app.MapGet("/", () => "Hello from Docker!");
```

For now, this is enough. Along the way, you will add database support as well, and connect it to a PostgreSQL instance(that runs on a separate container).

Let's Containerize this .NET 10 API now!

### Dockerfile

I prefer using VS Code for writing Dockerfile, YAML, and anything apart from C#.

Navigate to the folder where the `csproj` file exists, and create a new file named `Dockerfile`.

```bash
# Use the .NET SDK image to build and run the app
FROM mcr.microsoft.com/dotnet/sdk:10.0

# Set the working directory
WORKDIR /app

# Copy everything to the container
COPY . ./

# Restore dependencies
RUN dotnet restore

# Build the app in Release configuration
RUN dotnet publish -c Release -o release

# Set the working directory to the output directory
WORKDIR /app/release

# Set the entry point to the published app
ENTRYPOINT ["dotnet", "DockerEssentials.dll"]
```

Here is a line by line explanation.

- FROM: Specifies the base image to build upon. Here, it uses the official .NET SDK 8.0 image from Microsoft's container registry (mcr.microsoft.com/dotnet/sdk:10.0). This image includes the .NET SDK necessary to build and publish .NET applications.
- WORKDIR: Sets the working directory inside the container to /app. Subsequent instructions will be executed relative to this directory.
- COPY: Copies the current directory (.) from the host machine (where the Docker build command is run) to the /app directory inside the container. This assumes the Dockerfile is in the root of your project directory.
- RUN: Executes a command (dotnet restore) during the build process. This command restores the NuGet packages required by the .NET application, based on the \*.csproj files found in the current directory.
- RUN: Executes another command (dotnet publish) to build the .NET application in Release configuration (-c Release) and publish the output (-o release) to the /app/release directory inside the container. This step compiles the application and prepares it for deployment.
- WORKDIR: Changes the working directory to /app/release inside the container. This prepares for the next step to set the entry point.
- ENTRYPOINT: Specifies the command to run when the container starts. Here, it sets the entry point to execute the .NET application by running dotnet DockerEssentials.dll. Adjust DockerEssentials.dll to match the name of your published application's DLL.

### .dockerignore

Let's also add another file to improve the build process. Name it `.dockerignore`.

```bash
# Ignore build artifacts
bin/
obj/

# Ignore Visual Studio files
.vscode/
.vs/

# Ignore user-specific files
*.user
*.suo
```

Ok, so what does this file do exactly?

The `.dockerignore` file is used to specify files and directories that should be excluded from Docker builds. It works similarly to `.gitignore` in Git, allowing you to control which files and directories are not sent to the Docker daemon during the build process.

Here is how it helps:

1. Optimizing Build Context: When you run the docker build, Docker sends the entire directory (referred to as the build context) to the Docker daemon. This includes all files and directories in that directory. If your project directory contains unnecessary files (e.g., temporary files, logs, large datasets), sending them to the Docker daemon can slow down the build process and increase the size of your Docker image unnecessarily.
2. Excluding Unnecessary Files: This reduces the size of the build context, speeding up builds and ensuring that your Docker images only contain necessary files.
3. Improving Security: By excluding sensitive files or directories (e.g., configuration files with passwords, SSH keys) from the build context, you reduce the risk of inadvertently including them in your Docker images.

### Docker Build

Ok, so now that you have both the Application and the Dockerfile ready, let's build a Docker Image out of it. Open the terminal at the directory where the Dockerfile exists, and run the following.

```
docker build -t docker-essentials .
```

- `docker build`: This is the Docker command to build an image from a Dockerfile.
- `-t docker-essentials`: The `-t` flag allows you to tag your image with a name. In this case, the image will be tagged as `docker-essentials`.
- `.`: This specifies the build context. The `.` indicates that the current directory is the build context, where Docker will look for the Dockerfile.

This should build an image into your local Docker Library. Once completed, switch to Docker Desktop.

As you can see, you have a new image named `docker-essentials`. But, do you see the size of the image? A Whooping 902 Mb! But you just had a single GET Endpoint, right? So, what went wrong? And how do you optimize the image size?

## Multi-Stage Docker Build - Optimize Image Size

Docker has this awesome feature called multi-stage build, which as the name suggests creates multiple stages during a build process. If you closely see the Dockerfile, you have this following step,

```
FROM mcr.microsoft.com/dotnet/sdk:10.0
```

Here, `dotnet/sdk:10.0` is the base image. SDK images are usually larger is size as it contains additional tools and files to help build/debug code. However, in production, you don't need the SDK Image. You just need the .NET runtime to make your containers run.

Multi-stage Docker builds help by allowing you to use multiple stages in your Dockerfile to separate the build environment from the runtime environment.

The root cause of larger size of the image is that you have included everything within the SDK to the generated image. This is not required, and the application only needs the .NET Runtime to function.

In a multi-stage build, you define multiple FROM statements in your Dockerfile. Each FROM statement initializes a new build stage. You can then selectively copy artifacts from one stage to another using the COPY `--from=<stage>` directive.

Let's optimize the Dockerfile now.

```bash
# Stage 1: Build the application
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app
COPY . ./
RUN dotnet restore
RUN dotnet publish -c Release -o /app/out

# Stage 2: Create the runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# Copy the build output from the build stage
COPY --from=build /app/out .
ENTRYPOINT ["dotnet", "DockerEssentials.dll"]
```

I have segregated the Dockerfile into 2 stages,

1. Build: Here, you will still use the SDK as the base image, continue to restore the packages, and publish the .NET Application to a specific directory.
2. Image Creation: Here is where the magic happens. So, instead of using the SDK as the base image, you will use the `aspnet:10.0` as the base image of this stage. This is an image purely with all the tools required for runtime purposes only, and nothing additional. You then copy the output of the previous stage, which contains the binaries of the published .NET application to the root, and run the application.

This way you can completely avoid the unnecessary files and tools that had come as part of the SDK. Let's run the docker build command now. Before this, make sure to delete the image that you created earlier.

```
docker build -t docker-essentials .
```

**900+ MB down to under 220 MB!** Multi-Stage Build for Docker has saved us almost 75% of Size!

Further optimizations are still possible, but it comes at a cost. For most of the use cases, this is a good enough optimization approach.

## Run the .NET Docker Image

```
docker run --name docker-essentials -d -p 5080:8080 docker-essentials
```

You will name the docker container as `docker-essentials` and run it in detached mode. By default, from .NET 10 the exposed port is 8080 and no longer 80. I am mapping the container port 8080 to the localhost port 5080. This means that if you navigate to `localhost:5080`, you should be able to access the .NET 10 Web API endpoint. Also, ensure to specify the name of the image you intend to run. In the case, the image name is `docker-essentials`.

As you can see from the logs, you have the sample .NET app running as a Docker Container at 5080 local port. Let's try to hit the API.

Everything working as expected.

## Publish Docker Image

To publish a Docker image to a Docker registry such as Docker Hub, you would typically follow these steps:

1. Tag the Docker image: Ensure the image has a tag that follows the format repository/image:tag.
2. Login to the Docker registry: Use the docker login command to authenticate.
3. Push the image to the registry: Use the docker push command to upload the image.

Here are the required commands.

```bash
docker tag docker-essentials:latest <username>/docker-essentials:latest
docker login
docker push <username>/docker-essentials:latest
```

Once the push is completed, log in to your Docker Hub, navigate to repositories, and you can see the newly pushed image.

Pushing the image is only half the job. It is sitting in a registry now, and nothing is running it. You still need a server.

The cheapest setup I have found for actually running a .NET container in production is a single VPS with a self-hosted PaaS on top of it. You get the push-to-deploy workflow and automatic HTTPS, but the bill is one fixed server price instead of a per-service meter. I run a real multi-tenant app that way for around $40 a month. New DigitalOcean accounts get sign-up credit if you [create an account with my referral link](https://m.do.co/c/e39ab4f1bb79), which is enough to try the whole thing before you pay anything.

## Docker Compose

Let's step up the Docker game now! I will add some database interaction to my .NET Sample App. I will use the source code from a previous article, which already has some PostgreSQL database-based CRUD Endpoints. You can refer to this article - [In-Memory Caching in ASP.NET Core for Better Performance](https://codewithmukesh.com/blog/in-memory-caching-in-aspnet-core/).

I have made some modifications to remove the caching behavior. However, here is what you have as of now.

- .NET 10 Web API with CRUD Operations.
- Products table.
- Entity Framework Core.
- Connects to local PostgreSQL instance.

So, the ask is to containerize this application and also to include the database as part of the docker workflow.

First, let's build the Docker Image from the newly updated source code. Once built, let's tag the image to the latest version, and push it to Docker Hub. You can use the commands that you used in the previous steps.

Now, you will have to write a Docker Compose file that can run 2 containers, that is the .NET Web API, as well as a PostgreSQL instance.

At the root of the solution, create a new folder named compose, and add a new file `docker-compose.yml`.

```bash
version: "4"
name: docker_essentials
services:
  webapi:
    image: iammukeshm/docker-essentials:latest
    pull_policy: always
    container_name: webapi
    networks:
      - docker_essentials
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - ConnectionStrings__dockerEssentials=Server=postgres;Port=5433;Database=dockerEssentials;User Id=pgadmin;Password=pgadmin
    ports:
      - 5080:8080
    depends_on:
      postgres:
        condition: service_healthy
    restart: on-failure

  postgres:
    container_name: postgres
    image: postgres:15-alpine
    networks:
      - docker_essentials
    environment:
      - POSTGRES_USER=pgadmin
      - POSTGRES_PASSWORD=pgadmin
      - PGPORT=5433
    ports:
      - 5433:5433
    volumes:
      - postgresdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U pgadmin"]
      interval: 10s
      timeout: 5s
      retries: 5

volumes:
  postgresdata:

networks:
  docker_essentials:
    name: docker_essentials
```

This file includes services for a web API and a PostgreSQL database, setting up their interactions, environment variables, networking, and volumes. Here's a detailed breakdown:

- `version: "4"`: Specifies the version of the Docker Compose file format. Docker Compose v4 is an advanced version, that provides more features and improvements.
- `name: docker_essentials`: Sets the name for the Docker Compose application.

You have 2 services defined.

### webapi service

- `image: iammukeshm/docker-essentials:latest`: Uses the specified Docker image for the web API service, pulling the latest version.
- `pull_policy: always`: Ensures the image is pulled every time the container starts.
- `container_name: webapi`: Names the container webapi.
- `networks`: Adds the service to the docker_essentials network, enabling communication with other services in the network.
- `environment`: Sets environment variables inside the container.
  - `ASPNETCORE_ENVIRONMENT=Development:` Configures the ASP.NET Core environment to Development.
  - `ConnectionStrings__dockerEssentials=Server=postgres;Port=5433;Database=dockerEssentials;User Id=pgadmin;Password=pgadmin`: Sets the connection string for the application to connect to the PostgreSQL database which will run as a separate docker container.
- `ports`: Maps port 5080 on the host to port 8080 in the container.
- `depends_on`: Ensures the webapi service depends on the postgres service being healthy before starting.
  - `condition: service_healthy`: Waits for the PostgreSQL service to be healthy before starting the webapi service.
- `restart: on-failure`: Restarts the container if it fails.

### postgresql service

- `container_name: postgres`: Names the container postgres.
- `image: postgres:15-alpine:` Uses the postgres:15-alpine image, which is a lightweight version of PostgreSQL.
- `networks`: Adds the service to the docker_essentials network.
- `environment`: Sets environment variables for PostgreSQL.
  - `POSTGRES_USER=pgadmin`: Creates a PostgreSQL user pgadmin.
  - `POSTGRES_PASSWORD=pgadmin`: Sets the password for the pgadmin user.
  - `PGPORT=5433`: Sets the PostgreSQL server to listen on port 5433.
- `ports`: Maps port 5433 on the host to port 5433 in the container.
- `volumes`: Mounts the volume postgresdata to persist database data.
  - `postgresdata:/var/lib/postgresql/data`: Maps the host volume postgresdata to the container path /var/lib/postgresql/data.
- `healthcheck`: Defines a health check to determine if the PostgreSQL service is ready.
  - `test: ["CMD-SHELL", "pg_isready -U pgadmin"]`: Runs the pg_isready command to check if the PostgreSQL service is ready.
  - `interval: 10s`: Runs the health check every 10 seconds.
  - `timeout: 5s`: Sets the timeout for the health check to 5 seconds.
  - `retries: 5`: Retries the health check up to 5 times before considering the service unhealthy.

With that done, let's run a new command.

```
docker compose up -d
```

This runs all the instructions from the docker-compose.yml file in a detached mode. This command pulls the images as required, and spins up the containers as per the configuration.

As you can see, you have both the containers up and running! I have also tested the API endpoints, and it works as expected.

## Other Docker Compose Commands

### Remove Containers

To stop/remove the containers/services related to a specific docker-compose file, run the following.

```
docker compose down
```

### Run Specific Services

To run a specific service from a compose file, run the following.

```
docker compose up -d postgres
```

This spins up the `postgres` container.

That's it for this guide. Was it helpful?

## Dockerfile vs SDK Containerization: My Decision Matrix

In .NET 7+, you can containerize a .NET app **without writing a Dockerfile** using `dotnet publish /t:PublishContainer`. The SDK-based approach is faster to set up and produces optimized images by default. So when do you actually need a Dockerfile?

| Scenario | Dockerfile | SDK (`PublishContainer`) | Verdict |
|----------|-----------|--------------------------|---------|
| **Simple ASP.NET Core API** | ✅ | ✅ | SDK - less to maintain, the SDK builds optimized multi-arch chiseled images by default |
| **Need custom build steps** (npm, system packages, native deps) | ✅ | ❌ | Dockerfile - SDK can't run arbitrary RUN commands |
| **Need multi-stage with extra tooling** (ML model bake-in, asset pipeline) | ✅ | ❌ | Dockerfile - more layer control |
| **Multi-arch builds (linux/amd64 + linux/arm64)** | ✅ | ✅ | SDK - one MSBuild property (`ContainerImageTags + RuntimeIdentifiers`) handles both |
| **Strict image-size minimization** (chiseled / distroless) | ✅ | ✅ | SDK - chiseled images are the default with `ChiseledUbuntu` base; Dockerfile requires manual base selection |
| **Custom non-root user setup** | ✅ | ✅ | Either - SDK uses `app` user by default; Dockerfile gives full control |
| **CI/CD with `docker build` cache layers** | ✅ | ❌ | Dockerfile - SDK doesn't expose Docker layer cache |
| **Legacy or polyglot repo** (mixed Node + .NET) | ✅ | ❌ | Dockerfile - more flexible across stacks |

**My take**: I default to **SDK-based containerization** (`dotnet publish /t:PublishContainer`) for new .NET 10 services. It produces a chiseled, multi-arch, non-root image with ~5 lines of MSBuild config and no Dockerfile to maintain. I reach for a Dockerfile only when I need custom build steps (native deps, asset compilation) or when the team is already standardized on Dockerfile-based pipelines across stacks.

## Key Takeaways

- **Use multi-stage builds.** SDK image (`mcr.microsoft.com/dotnet/sdk:10.0`) for build, runtime image (`mcr.microsoft.com/dotnet/aspnet:10.0`) for run. Final image stays under ~100 MB instead of ~700 MB.
- **The runtime image is `aspnet:10.0`, not `sdk:10.0`.** Using the SDK image as your runtime is the most common mistake - it's 5x larger and exposes the build toolchain in production.
- **Docker Compose is the right answer for multi-container local dev.** API + PostgreSQL + Redis + RabbitMQ in one `docker-compose.yml` beats running each service manually.
- **Always run as non-root in production.** Add `USER app` to the Dockerfile (or use SDK containerization which does this by default). Container escapes that gain root access on the host are still real.
- **Chiseled images cut size by 70-80%.** Use `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` for production - ~25 MB final image with no shell, no package manager, smaller attack surface.
- **`.dockerignore` matters.** Exclude `bin/`, `obj/`, `.git/`, `node_modules/` - missing this can balloon the build context from KB to GB.

## Frequently Asked Questions

## Troubleshooting Common Docker Issues with .NET

### "It works in my Dockerfile but fails when run"

The classic cause: hardcoded `localhost` in connection strings. Inside a container, `localhost` refers to the container itself, not your host machine or another container. In Docker Compose, use the **service name** as the hostname (e.g., `Host=postgres` instead of `Host=localhost`). For containers running outside Compose that need to reach the host, use `host.docker.internal` on Docker Desktop.

### Image size is 700 MB instead of 100 MB

You're using the SDK image as your runtime. Switch to a multi-stage build:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# ... build steps ...

FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=build /app/out .
ENTRYPOINT ["dotnet", "MyApp.dll"]
```

The runtime image is ~5x smaller than the SDK image. For even smaller images, use `aspnet:10.0-noble-chiseled` (~25 MB final).

### "Permission denied" running the app inside the container

You're running as a non-root user but trying to write to a directory owned by root. Either chown the directory in the Dockerfile (`RUN chown -R app:app /app/data`) before switching to the `app` user, or write to `/tmp` or `/home/app` which the non-root user already owns.

### `dotnet ef` migrations fail inside the container

The migrations CLI needs the SDK, not the runtime. Either run migrations from your build pipeline (recommended) using `dotnet ef migrations script` or `dotnet ef database update`, or use a separate migration container based on `sdk:10.0` that exits after applying migrations.

### PostgreSQL container starts but the API can't connect

Three common causes:
1. **No `depends_on` with healthcheck** - the API tries to connect before Postgres is ready. Add a `healthcheck` to the postgres service and `depends_on: { postgres: { condition: service_healthy } }` on the API.
2. **Wrong hostname** - use the service name (`postgres`), not `localhost` or the container ID.
3. **Port binding confusion** - inside the Compose network, use the **internal** PostgreSQL port (5432). The `ports: 5432:5432` mapping is for accessing PostgreSQL from your **host**, not from other containers.

### `.dockerignore` not being respected

`.dockerignore` must be at the root of the **build context**, which is the directory you run `docker build` from (or the `context:` directory in Compose), not next to the Dockerfile if they live in different places. Check with `docker build --no-cache --progress=plain .` and look at what's in the build context.

## Related Articles

## Closing Thoughts

In this comprehensive Docker guide for .NET 10 developers, I've covered nearly everything you need to embark on your Docker journey - the fundamentals of Docker, how it operates, its building blocks, Docker Desktop, and the most essential Docker CLI commands.

You also took practical steps by containerizing a .NET 10 sample application with a Dockerfile, building the image, and publishing your custom image to Docker Hub. Building on that foundation, you introduced PostgreSQL database interaction and wrote a Docker Compose file capable of spinning up multiple containers, demonstrating how to manage a multi-container environment effectively.

For .NET 10 projects starting today, my recommendation is to skip the Dockerfile entirely and use SDK-based containerization with `dotnet publish /t:PublishContainer` - it produces an optimized chiseled multi-arch image with no Dockerfile to maintain. Reach for a Dockerfile only when you need custom build steps or layer-cache control in CI.

I hope this guide has significantly expanded your Docker knowledge. Please share it with your colleagues!

Happy Coding :)
