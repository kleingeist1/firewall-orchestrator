
import logging
import common


def csv_dump_nw_obj(nw_obj, import_id):
    result_line = '"' + import_id + '"' + common.csv_delimiter  # control_id
    result_line += '"' + nw_obj['obj_name'] + '"' + common.csv_delimiter  # obj_name
    result_line += '"' + nw_obj['obj_typ'] + '"' + common.csv_delimiter  # ob_typ
    result_line += '"' + nw_obj['obj_member_names'] + '"' + common.csv_delimiter  # obj_member_names
    result_line += '"' + nw_obj['obj_member_refs'] + '"' + common.csv_delimiter  # obj_member_refs
    result_line += common.csv_delimiter  # obj_sw
    if nw_obj['obj_typ'] == 'group':
        result_line += common.csv_delimiter  # obj_ip for groups = null
    else:
        result_line += '"' + nw_obj['obj_ip'] + '"' + common.csv_delimiter  # obj_ip
    if 'obj_ip_end' in nw_obj:
        result_line += '"' + nw_obj['obj_ip_end'] + '"' + common.csv_delimiter         # obj_ip_end
    else:
        result_line += common.csv_delimiter
    result_line += '"' + nw_obj['obj_color'] + '"' + common.csv_delimiter  # obj_color
    result_line += '"' + nw_obj['obj_comment'] + '"' + common.csv_delimiter  # obj_comment
    result_line += common.csv_delimiter  # result_line += '"' + nw_obj['obj_location'] + '"' + csv_delimiter       # obj_location
    if 'obj_zone' in nw_obj:
        result_line += '"' + nw_obj['obj_zone'] + '"' + common.csv_delimiter           # obj_zone
    else:
        result_line += common.csv_delimiter
    result_line += '"' + nw_obj['obj_uid'] + '"' + common.csv_delimiter  # obj_uid
    result_line += common.csv_delimiter  # last_change_admin
    # add last_change_time
    result_line += common.line_delimiter
    return result_line


# collect_nw_objects writes nw objects info into global nw_objects dict
def collect_nw_objects(object_table, nw_objects):
    result = ''  # todo: delete this line
    # nw_obj_tables = ['hosts', 'networks', 'address-ranges', 'groups', 'gateways-and-servers', 'simple-gateways']
    nw_obj_type_to_host_list = [
        'address-range', 'multicast-address-range',
        'simple-gateway', 'simple-cluster', 'CpmiVsClusterNetobj', 'CpmiAnyObject', 
        'CpmiClusterMember', 'CpmiGatewayPlain', 'CpmiHostCkp', 'CpmiGatewayCluster', 'checkpoint-host' 
    ]

    if object_table['object_type'] in common.nw_obj_table_names:
        for chunk in object_table['object_chunks']:
            for obj in chunk['objects']:
                members = ''
                ip_addr = ''
                member_refs = ''
                member_names = ''
                if 'members' in obj:
                    for member in obj['members']:
                        member_refs += member + common.list_delimiter
                    member_refs = member_refs[:-1]
                ip_addr = common.get_ip_of_obj(obj)
                obj_type = obj['type']
                if obj_type == 'address-range' or obj_type == 'multicast-address-range':
                    obj_type = 'ip_range'  # TODO: change later?
                    #logging.debug("parse_network::collect_nw_objects - found range object '" + obj['name'] + "' with ip: " + ip_addr)
                    if '-' in ip_addr:
                        first_ip, last_ip = ip_addr.split('-')
                        nw_objects.extend([{'obj_uid': obj['uid'], 'obj_name': obj['name'], 'obj_color': obj['color'],
                                            'obj_comment': obj['comments'],
                                            'obj_typ': obj_type, 'obj_ip': first_ip, 'obj_ip_end': last_ip,
                                            'obj_member_refs': member_refs, 'obj_member_names': member_names}])
                    else:
                        logging.warning("parse_network::collect_nw_objects - found range object '" + obj['name'] + "' without hyphen: " + ip_addr)
                else:
                    if (obj_type in nw_obj_type_to_host_list):
                        obj_type = 'host'
                    nw_objects.extend([{'obj_uid': obj['uid'], 'obj_name': obj['name'], 'obj_color': obj['color'],
                                    'obj_comment': obj['comments'],
                                    'obj_typ': obj_type, 'obj_ip': ip_addr,
                                    'obj_member_refs': member_refs, 'obj_member_names': member_names}])


# for members of groups, the name of the member obj needs to be fetched separately (starting from API v1.?)
def resolve_nw_uid_to_name(uid, nw_objects):
    # return name of nw_objects element where obj_uid = uid
    for obj in nw_objects:
        if obj['obj_uid'] == uid:
            return obj['obj_name']
    return 'ERROR: uid ' + uid + ' not found'


def add_member_names_for_nw_group(idx, nw_objects):
    member_names = ''
    group = nw_objects.pop(idx)
    obj_member_refs = group['obj_member_refs'].split(common.list_delimiter)
    for ref in obj_member_refs:
        member_name = resolve_nw_uid_to_name(ref, nw_objects)
        # print ("found member of group " + group['obj_name'] + ": " + member_name)
        member_names += member_name + common.list_delimiter
    group['obj_member_names'] = member_names[:-1]
    nw_objects.insert(idx, group)
