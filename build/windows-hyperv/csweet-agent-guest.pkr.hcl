packer {
  required_plugins {
    hyperv = {
      source  = "github.com/hashicorp/hyperv"
      version = "= 1.1.5"
    }
  }
}

variable "iso_url" {
  type = string
}

variable "iso_checksum" {
  type = string
}

variable "switch_name" {
  type = string
}

variable "ssh_private_key_file" {
  type = string
}

variable "seed_iso_path" {
  type = string
}

variable "guest_publish_directory" {
  type = string
}

variable "output_directory" {
  type = string
}

variable "vm_name" {
  type = string
}

source "hyperv-iso" "csweet_agent_guest" {
  vm_name                          = var.vm_name
  generation                       = 2
  cpus                             = 2
  memory                           = 4096
  disk_size                        = 16384
  disk_block_size                  = 1
  enable_dynamic_memory            = false
  enable_secure_boot               = true
  secure_boot_template             = "MicrosoftUEFICertificateAuthority"
  enable_virtualization_extensions = false
  switch_name                      = var.switch_name
  headless                         = true
  first_boot_device                = "DVD"
  iso_url                          = var.iso_url
  iso_checksum                     = var.iso_checksum
  secondary_iso_images             = [var.seed_iso_path]
  output_directory                 = var.output_directory
  skip_compaction                  = false
  communicator                     = "ssh"
  ssh_username                     = "csweet-image"
  ssh_private_key_file             = var.ssh_private_key_file
  ssh_timeout                      = "35m"
  shutdown_command                 = "sudo -n shutdown -P now"
  shutdown_timeout                 = "10m"
  boot_wait                        = "5s"
  boot_keygroup_interval           = "10ms"
  boot_command = [
    "<esc><wait>c<wait>",
    "linux /casper/vmlinuz autoinstall ---<enter><wait>",
    "initrd /casper/initrd<enter><wait>",
    "boot<enter>"
  ]
}

build {
  sources = ["source.hyperv-iso.csweet_agent_guest"]

  provisioner "file" {
    source      = "${var.guest_publish_directory}/CSweet.AgentRuntime.Guest"
    destination = "/tmp/CSweet.AgentRuntime.Guest"
  }

  provisioner "shell" {
    script          = abspath("${path.root}/provision-guest.sh")
    execute_command = "chmod +x '{{ .Path }}'; sudo -n bash '{{ .Path }}'"
  }
}
